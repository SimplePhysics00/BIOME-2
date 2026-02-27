using Biome2.World;
using Biome2.Diagnostics;
using Biome2.FileLoading.Models;
using Biome2.World.CellGrid;

namespace Biome2.Simulation;

/// <summary>
/// Owns simulation state and stepping.
/// For now it does nothing. Later it will load rules, run multithread jobs, and collect stats.
/// </summary>
public sealed class SimulationController : IDisposable {
    private WorldState _world;
	public WorldState World => _world;

    // Optional path of the last loaded rules file. UI sets this when a file is loaded.
    public string? LastLoadedRulesFilePath { get; set; }

    private readonly Performance _perf;

	private readonly object _stepLock = new();

    // Background stepping
    private CancellationTokenSource? _cts;
    private Task? _bgTask;
    public bool IsBackgroundRunning => _bgTask != null && !_bgTask.IsCompleted && _cts != null && !_cts.IsCancellationRequested;

    // Event raised when the world instance is replaced by ApplyRules.
    public event Action<WorldState>? WorldReplaced;

    // Loaded rules for the simulation. Populated by ApplyRules.
    private List<Models.SimulationRuleModel> _rules = [];
    public IReadOnlyList<Models.SimulationRuleModel> Rules => _rules;

    // Last applied WorldModel request (from file). Used to restart the world to its initial state.
    private WorldModel? _lastWorldModel;

    // Edge handling mode (influences neighbor lookups)
    private EdgeMode _edgeMode = EdgeMode.BORDER;

    // Indexed rules: key = (layerIndex, originSpeciesIndex)
    private Dictionary<(int layer, int origin), List<Models.SimulationRuleModel>> _ruleIndex = [];
    // Layers that have any rules (for quick skipping)
    private HashSet<int> _layersWithRules = [];

    public SimulationClock Clock { get; } = new();

    // Reusable thread-local RNG to avoid allocating one each step.
    private readonly ThreadLocal<Random> _threadLocalRand;

    // Lightweight struct for move intents (declared at class scope to avoid local-type restrictions)
    private readonly struct MoveIntent {
        public readonly int Sx, Sy, Dx, Dy, SrcResult;
        public readonly byte Species;
        public MoveIntent(int sx, int sy, int dx, int dy, byte species, int srcResult) {
            Sx = sx; Sy = sy; Dx = dx; Dy = dy; Species = species; SrcResult = srcResult;
        }
    }

    /// <summary>
    /// Restart the world to the initial state but with explicit width/height.
    /// If a last-applied WorldModel exists, it will be re-applied with the
    /// supplied dimensions overriding the file values.
    /// </summary>
    public void RestartWorld(int width, int height, int depth)
    {
        if (_lastWorldModel != null)
        {
            var config = new WorldConfigModel(
                width: width,
                height: height,
                depth: depth,
                gridTopology: _lastWorldModel.Config.GridTopology,
                edgeMode: _lastWorldModel.Config.Edges,
                paused: _lastWorldModel.Config.Paused
            );

			var req = new WorldModel(
                config: config,
				species: _lastWorldModel.Species,
                layers: _lastWorldModel.Layers,
                rules: _lastWorldModel.Rules
			);

            ApplyRules(req);
            return;
        }
    }

    // Immediate placement for visual feedback; writes both current and next buffers. Caller holds lock.
    public void PlaceImmediate(int layerIndex, int x, int y, byte speciesValue) {
        lock (_stepLock) {
            _world.PlaceImmediate(layerIndex, x, y, speciesValue);
        }
    }

    // Set selected species indices snapshot used by placement requests.
    public void SetSelectedSpeciesIndices(int[] indices) {
        _world.SetSelectedSpeciesIndices(indices);
    }

    // Enqueue a lightweight placement request (species chosen by WorldState snapshot during application).
    public void EnqueuePlacementRequest(int x, int y) {
        _world.EnqueuePlacementRequest(x, y);
    }

    public SimulationController(WorldState world, Performance perf) {
        _world = world;
        _perf = perf;

        // Start paused, since we have no rules yet.
        Clock.Paused = true;

        // Start background stepping so simulation can run as fast as possible independent of render.
        _cts = new CancellationTokenSource();
        _bgTask = Task.Run(() => BackgroundLoop(_cts.Token));

        // Create the thread-local RNG once to reduce per-step allocations.
        _threadLocalRand = new ThreadLocal<Random>(() => new Random(Random.Shared.Next()));

        // TODO: create default species and rules for Conway's Game of Life
    }

	public void Dispose() {
		if (_cts != null) {
			try { _cts.Cancel(); } catch { }
			try { _bgTask?.Wait(1000); } catch { }
			_cts.Dispose();
			_cts = null;
		}
	}

	private async Task BackgroundLoop(CancellationToken token) {
		// Run as fast as possible when not paused. Respect DelayTime as an optional extra pause per-step.
		try {
			while (!token.IsCancellationRequested) {
				// Apply any pending manual placements queued by the UI into the next buffers now that they are prepared.
				_world.ApplyPendingPlacements();

				if (Clock.Paused) {
					await Task.Delay(1, token).ConfigureAwait(false);
					continue;
				}

				// Perform a single step. Ensure not concurrently executed with any manual Update calls.
				lock (_stepLock) {
					StepOnce();
				}

				// Honor optional per-step delay (in seconds) if set; otherwise continue immediately for max speed.
				if (Clock.DelayTime > 0.0f) {
					int ms = (int)Math.Round(Clock.DelayTime * 1000.0f);
					if (ms > 0) await Task.Delay(ms, token).ConfigureAwait(false);
				}
			}
		} catch (OperationCanceledException) {
			// expected on cancellation
		}
	}

	// Apply rules and species from a parsed file world model.
    public void ApplyRules(WorldModel worldModel) {
        // Keep a copy of the last applied world model so the world can be restarted to this initial state.
        _lastWorldModel = worldModel;

        // Immediately apply pause setting so background loop respects it quickly.
        Clock.Paused = worldModel.Config.Paused;

        // Determine sizing: prefer file-provided positive values, otherwise keep current world values.
        int newWidth = worldModel.Config.Width > 0 ? worldModel.Config.Width : Math.Max(1, _world.WidthCells);
        int newHeight = worldModel.Config.Height > 0 ? worldModel.Config.Height : Math.Max(1, _world.HeightCells);
        int newDepth = worldModel.Config.HexDepth > 0 ? worldModel.Config.HexDepth : Math.Max(1, _world.DepthCells);
        
		int newLayerCount = (worldModel.Layers != null && worldModel.Layers.Count > 0) ? worldModel.Layers.Count : Math.Max(1, _world.LayerCount);

        // Construct new runtime world state using requested topology.
        var topology = worldModel.Config.GridTopology;

		var newWorld = new WorldState(newWidth, newHeight, newLayerCount, topology, newDepth);

        // Preserve previously selected active layer index where possible so the view
        // remains on the same layer after applying new rules (unless the new world
        // has fewer layers, in which case clamp to the last available layer).
        int prevActive = _world.ActiveLayerIndex;
        newWorld.ActiveLayerIndex = Math.Clamp(prevActive, 0, Math.Max(0, newWorld.LayerCount - 1));

		// If species list provided, apply it and initialize grids to species index 0.
		if (worldModel.Species != null && worldModel.Species.Count > 0) {
            newWorld.SetSpeciesList(worldModel.Species);
            foreach (var layer in newWorld.Layers) {
                layer.Grid.Clear(0);
            }
        }

        // If layer names provided, copy them into the world model.
        if (worldModel.Layers != null && worldModel.Layers.Count > 0) {
            int min = Math.Min(worldModel.Layers.Count, newWorld.Layers.Count);
            for (int i = 0; i < min; i++) newWorld.Layers[i].Name = worldModel.Layers[i] ?? string.Empty;
            if (worldModel.Layers.Count > newWorld.Layers.Count) Logger.Warn("More layer names provided by rules file than world contains; extra layer names ignored.");
            else if (worldModel.Layers.Count < newWorld.Layers.Count) Logger.Warn("Fewer layer names provided by rules file than world contains; remaining layers keep default names.");
        }

        // Prepare rules and edge mode
        var fileRules = worldModel.Rules ?? [];
        var newEdgeMode = worldModel.Config.Edges;

        // Convert file models to simulation models using the new world for name resolution.
        var (simRules, simIndex, simLayersWithRules) = RuleSetBuilder.Build(fileRules, newWorld);

        // Swap world and rules under lock to avoid racing with stepping.
        lock (_stepLock) {
            _world = newWorld;
            _rules = simRules;
            _edgeMode = newEdgeMode;
            _ruleIndex = simIndex;
            _layersWithRules = simLayersWithRules;
        }

        // Notify subscribers after swap. Keep this outside the lock to avoid deadlocks.
        try {
            WorldReplaced?.Invoke(_world);
        } catch (Exception ex) {
            Logger.Error($"Exception while notifying WorldReplaced subscribers: {ex.Message}");
        }
    }

	public void Update(float dtSeconds) {
		// If background stepping is active, skip manual stepping to avoid contention.
		if (IsBackgroundRunning) return;

		var steps = Clock.ConsumeSteps(dtSeconds);
		for (int i = 0; i < steps; i++) {
			lock (_stepLock) {
				StepOnce();
			}
		}
	}

	private void StepOnce() {
        // Prepare next buffers. All layers need to be processed or else they won't capture manual additions correctly
        for (int li = 0; li < _world.Layers.Count; li++) {
            _world.Layers[li].Grid.CopyCurrentToNext();
        }

        // Use reusable thread-local RNG to avoid allocating one each step.
        var threadLocalRand = _threadLocalRand;

        // Process layers sequentially to avoid nested Parallel scheduling across layers.
        foreach (int layerIndex in _layersWithRules) {
            var layer = _world.Layers[layerIndex];
            var grid = layer.Grid;
            var rand = threadLocalRand.Value!;
            int total = grid.Width * grid.Height;

            // Per-layer mark-phase intent buffers. Use a fast-path for single intents to avoid
            // allocating a List for the common case where only one intent is produced per cell.
            var intentSingles = new System.Collections.Concurrent.ConcurrentBag<MoveIntent>();
            var intentLists = new System.Collections.Concurrent.ConcurrentBag<List<MoveIntent>>();

            // (MoveIntent struct is declared at class scope.)

            // Parallelize across cells within the layer.
            Parallel.For(0, total, idx => {
                var rand = threadLocalRand.Value!;
                int y = idx / grid.Width;
                int x = idx % grid.Width;
                if (!grid.IsValidCell(x, y)) return; // skip invalid positions for sparse topologies
                byte originValue = grid.GetCurrent(x, y);
                // Local state for produced intents. Fast-path for single intent avoids allocating a List.
                MoveIntent singleIntent = default;
                bool hasSingleIntent = false;
                List<MoveIntent>? localIntents = null;

                if (_ruleIndex.TryGetValue((layerIndex, originValue), out var candidates)) {
                    // Allocate neighbor and candidate buffers once per cell iteration (safer and reduces repeated stackallocs).
                    Span<int> nx = stackalloc int[8];
                    Span<int> ny = stackalloc int[8];
                    Span<int> cand = stackalloc int[8];
                    foreach (var rule in candidates) {
                        // If rule has coordinate limits, skip when current cell is outside them.
                        if (rule.XMin.HasValue && x < rule.XMin.Value) continue;
                        if (rule.XMax.HasValue && x > rule.XMax.Value) continue;
                        if (rule.YMin.HasValue && y < rule.YMin.Value) continue;
                        if (rule.YMax.HasValue && y > rule.YMax.Value) continue;

                        bool allReactantsMatch = CheckAllReactantsMatch(layer, y, x, rule);
                        if (!allReactantsMatch) continue;

                        if (rule.MoveSpeciesIndex >= 0) {
                            // Try to recruit an adjacent mover of the specified species into (x,y)
                            // Gather neighbor coords and pick uniformly at random among valid candidates.
                            int written = grid.GetNeighborCoordinates(x, y, _edgeMode, nx, ny);
                            int ci = 0;
                            for (int ni = 0; ni < written; ni++) {
                                int cx = nx[ni];
                                int cy = ny[ni];
                                if (cx < 0 || cy < 0) continue;
                                if (!grid.IsValidCell(cx, cy)) continue;
                                var v = grid.GetCurrent(cx, cy);
                                if (v == rule.MoveSpeciesIndex) cand[ci++] = ni;
                            }

                            if (ci > 0) {
                                // Apply move probability at mark time
                                if (rule.Probability >= 1.0 || rand.NextDouble() < rule.Probability) {
                                    int pick = cand[rand.Next(ci)];
                                    int sx = nx[pick];
                                    int sy = ny[pick];
									// srcResult: what the source cell should become after moving.
									// Use explicit NewSpeciesIndex when provided; otherwise default to the rule's origin species.
									int srcResult = rule.NewSpeciesIndex >= 0 ? rule.NewSpeciesIndex : rule.OriginSpeciesIndex;
                                    if (!hasSingleIntent && localIntents == null) {
                                        // store as single intent
                                        singleIntent = new MoveIntent(sx, sy, x, y, (byte)rule.MoveSpeciesIndex, srcResult);
                                        hasSingleIntent = true;
                                    } else {
                                        if (localIntents == null) {
                                            localIntents = new List<MoveIntent>(2);
                                            if (hasSingleIntent) {
                                                localIntents.Add(singleIntent);
                                                hasSingleIntent = false;
                                            }
                                        }
                                        localIntents.Add(new MoveIntent(sx, sy, x, y, (byte)rule.MoveSpeciesIndex, srcResult));
                                    }
                                    rule.IncrementOpCount();								}
                            }
                            // Move-style rules should NOT fall through to the standard replacement behavior.
                            // If no mover was found or the move probability check failed, the rule simply does nothing.
                            continue;
                        }

                        // Non-move replacement behavior
                        if (rule.NewSpeciesIndex >= 0) {
                            if (rule.Probability >= 1.0 || rand.NextDouble() < rule.Probability) {
                                if (rule.LayerIndex == layerIndex) {
                                    layer.Grid.SetNext(x, y, (byte) rule.NewSpeciesIndex);
                                } else {
                                    var targetLayer = _world.Layers[rule.LayerIndex];
                                    targetLayer.Grid.SetNext(x, y, (byte) rule.NewSpeciesIndex);
                                }

                                rule.IncrementOpCount();
                            }
                        }
                    }
                }

                if (localIntents != null) intentLists.Add(localIntents);
                else if (hasSingleIntent) intentSingles.Add(singleIntent);
            });

            // Commit phase: collect intents and resolve conflicts (randomly choose one per destination)
            // Build a dictionary keyed by destination index
            var destMap = new Dictionary<int, List<MoveIntent>>();
            foreach (var list in intentLists) {
                foreach (var it in list) {
                    int dIdx = it.Dx + it.Dy * grid.Width;
                    if (!destMap.TryGetValue(dIdx, out var l)) { l = new List<MoveIntent>(); destMap[dIdx] = l; }
                    l.Add(it);
                }
            }
            // Include single-intent fast-path entries
            foreach (var s in intentSingles) {
                int dIdx = s.Dx + s.Dy * grid.Width;
                if (!destMap.TryGetValue(dIdx, out var l)) { l = new List<MoveIntent>(); destMap[dIdx] = l; }
                l.Add(s);
            }

            // For each destination pick one candidate at random and apply move.
            // Ensure each source cell is used at most once (no duplication).
            var destKeys = destMap.Keys.ToList();
            // Shuffle destination processing order to keep fairness
            for (int i = destKeys.Count - 1; i > 0; i--) {
                int j = rand.Next(i + 1);
                var tmp = destKeys[i]; destKeys[i] = destKeys[j]; destKeys[j] = tmp;
            }

            var usedSources = new HashSet<int>();
            foreach (var dIdx in destKeys) {
                var dlist = destMap[dIdx];
                if (dlist.Count == 0) continue;

                // Filter out candidates whose source is already used
                var available = new List<MoveIntent>();
                foreach (var c in dlist) {
                    int sIdx = c.Sx + c.Sy * grid.Width;
                    if (!usedSources.Contains(sIdx)) available.Add(c);
                }

                if (available.Count == 0) continue;

                var chosen = available[rand.Next(available.Count)];
                int dx = dIdx % grid.Width;
                int dy = dIdx / grid.Width;
                // set destination next and clear source next
                grid.SetNext(dx, dy, chosen.Species);
                // set source to configured srcResult
                grid.SetNext(chosen.Sx, chosen.Sy, (byte)chosen.SrcResult);

                usedSources.Add(chosen.Sx + chosen.Sy * grid.Width);
            }
        }

		// Swap buffers after all rules evaluated
		foreach (var layer in _world.Layers) {
			layer.Grid.SwapBuffers();
		}

        // Record that a tick occurred for TPS tracking, but only after a rules file
        // has been loaded. This avoids skewing the max TPS from warmup/background activity
        // before a file is in use.
        try {
            if (!string.IsNullOrEmpty(LastLoadedRulesFilePath)) {
                _perf.RecordTick();
            }
        } catch { }
	}

	private bool CheckAllReactantsMatch(WorldLayer layer, int y, int x, Models.SimulationRuleModel rule) {
		bool allReactantsMatch = true;

        // Allocate neighbor index buffer once to avoid repeated stackalloc inside the loop (CA2014)
        Span<byte> neighborIdx = stackalloc byte[8];

        foreach (var react in rule.Reactants) {
			// If react.LayerIndex >= 0 then this reactant targets an exact layer cell at the same coordinates.
			if (react.LayerIndex >= 0) {
				int targetLayerIndex = react.LayerIndex;
				if (targetLayerIndex < 0 || targetLayerIndex >= _world.Layers.Count) { allReactantsMatch = false; break; }

				var targetGrid = _world.Layers[targetLayerIndex].Grid;

				// Bounds check for same-coordinate access
				if (x < 0 || x >= targetGrid.Width || y < 0 || y >= targetGrid.Height) { allReactantsMatch = false; break; }

                byte v = targetGrid.GetCurrent(x, y);
                if (react.Exclusion) {
                    // Exclusionary exact-layer reactant: must NOT be the specified species
                    if (v == react.SpeciesIndex) { allReactantsMatch = false; break; }
                } else {
                    if (v != react.SpeciesIndex) { allReactantsMatch = false; break; }
                }
				// For exact-layer reactants, Count/Sign semantics are treated as == by design when referencing a single cell.
				continue;
            } else {
                // ask the grid implementation to populate neighbors according to topology/edge-mode
                var targetGrid = layer.Grid;
                int written = targetGrid.GetNeighbors(x, y, _edgeMode, neighborIdx);
                // For rectangular grids written == 8; react.Check will use neighbors.Length as provided
                if (!react.Check(neighborIdx.Slice(0, written))) { allReactantsMatch = false; break; }
            }
		}

		return allReactantsMatch;
	}
}
