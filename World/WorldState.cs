using Biome2.Diagnostics;
using System;
using System.Linq;
using OpenTK.Mathematics;
using Biome2.FileLoading.Models;

namespace Biome2.World;

/// <summary>
/// The root world state container.
/// Keeps layers, dimensions, metadata, and hooks for history and statistics.
/// </summary>
public sealed class WorldState {
	// World defaults for first launch.
	private const int DefaultWorldWidthCells = 256;
	private const int DefaultWorldHeightCells = 256;
	private const int DefaultWorldLayerCount = 1;

	public int WidthCells { get; }
	public int HeightCells { get; }
	public int LayerCount { get; }

	private readonly List<WorldLayer> _layers = new();
	public IReadOnlyList<WorldLayer> Layers => _layers;

	public IReadOnlyList<string> LayerNames => _layers.Select(l => l.Name).ToList();

	// Layer viewing can swap which layer is currently visible.
	public int ActiveLayerIndex { get; set; } = 0;

	public WorldLayer ActiveLayer => _layers[ActiveLayerIndex];

    // Pending manual placements queued by UI. These are applied by the simulation at a safe point.
    // There are two kinds of pending entries:
    //  - explicit placements with a concrete species (used by PlaceImmediate)
    //  - lightweight placement requests (layer,x,y) where a species will be chosen from SelectedSpecies during application
    private readonly List<(int Layer, int X, int Y, byte Species)> _pendingPlacements = new();
    private readonly List<(int Layer, int X, int Y)> _placementRequests = new();
    private readonly object _pendingLock = new object();

    // Current selected species indices for UI brush. Snapshot read by simulation when applying placement requests.
    private int[] _selectedSpeciesIndices = Array.Empty<int>();

	// Species registry for this world. Each species index maps directly to the byte value
	// stored in CellGrid cells. The renderer will query this model for RGBA bytes.
	private List<SpeciesModel> _species = [];
	public IReadOnlyList<SpeciesModel> Species => _species;

    // Flattened RGBA8 palette (4 bytes per species) rebuilt when species list changes.
    private byte[] _speciesPalette = Array.Empty<byte>();

    // Event raised when the flattened RGBA8 palette changes. The payload is a
    // byte[] with length = (speciesCount * 4) in RGBA order. If no species are
    // defined, an empty array is provided.
    public event Action<byte[]>? SpeciesPaletteChanged;

    public WorldState(int widthCells, int heightCells, int layerCount) {
		// bound checking
		var _widthCells = widthCells;
		var _heightCells = heightCells;
		var _layerCount = layerCount;

		if (widthCells <= 0) {
			Logger.Error("WidthCells must be positive.");
			_widthCells = 1;
		}
		if (heightCells <= 0) {
			Logger.Error("HeightCells must be positive.");
			_heightCells = 1;
		}
		if (layerCount <= 0) {
			Logger.Error("LayerCount must be positive.");
			_layerCount = 1;
		}

		WidthCells = _widthCells;
		HeightCells = _heightCells;
		LayerCount = _layerCount;

		CreateLayers();
	}

	private void CreateLayers() {
		for (int i = 0; i < LayerCount; i++) {
			_layers.Add(new WorldLayer($"Layer {i}", WidthCells, HeightCells));
		}
	}

    public static WorldState CreateBlank() {
        return new WorldState(
			DefaultWorldWidthCells,
			DefaultWorldHeightCells,
			DefaultWorldLayerCount
		);
	}

	/// <summary>
	/// Replace the species list for this world. The species index is the byte stored in CellGrid.
	/// Rebuilds an internal flattened RGBA8 palette for fast lookups by renderer.
	/// </summary>
	public void SetSpeciesList(IEnumerable<SpeciesModel> species) {
		ArgumentNullException.ThrowIfNull(species);
		_species = [.. species];
		BuildSpeciesPalette();
	}

	public int GetLayerIndex(string name) {
		if (string.IsNullOrEmpty(name)) return -1;
		for (int i = 0; i < _layers.Count; i++) {
			if (string.Equals(_layers[i].Name, name, StringComparison.Ordinal)) return i;
		}
		return -1;
	}

	/// <summary>
	/// Return a copy of the flattened RGBA8 palette. Caller owns the array.
	/// </summary>
	public byte[] GetSpeciesPalette() => (byte[])_speciesPalette.Clone();

	/// <summary>
	/// Get the display name for a species index; returns empty string if out of range.
	/// </summary>
	public string GetSpeciesName(int index) =>
		(index >= 0 && index < _species.Count) ? _species[index].Name : string.Empty;

	/// <summary>
	/// Find the index for a species name; returns -1 when not found.
	/// </summary>
	public int GetSpeciesIndex(string name) {
		if (string.IsNullOrEmpty(name)) return -1;
		for (int i = 0; i < _species.Count; i++) {
			if (string.Equals(_species[i].Name, name, StringComparison.Ordinal))
				return i;
		}
		return -1;
	}

	/// <summary>
	/// Returns a ReadOnlySpan of 4 bytes containing RGBA8 for the given cell byte value.
	/// If the species list is empty returns a fallback color. Out-of-range indices are clamped
	/// to the last species.
	/// </summary>
	public ReadOnlySpan<byte> GetSpeciesColorBytes(byte value) {
		if (_species.Count == 0) {
			Logger.Error("At least one species must be defined to get color bytes.");
			return new ReadOnlySpan<byte>();
		}

		int idx = value < _species.Count ? value : (_species.Count - 1);
		int off = idx * 4;
		return new ReadOnlySpan<byte>(_speciesPalette, off, 4);
	}

	private void BuildSpeciesPalette() {
		if (_species.Count == 0) {
			_speciesPalette = [];
			SpeciesPaletteChanged?.Invoke([]);
			return;
		}

		_speciesPalette = new byte[_species.Count * 4];
		for (int i = 0; i < _species.Count; i++) {
			var c = _species[i].Color;

			int off = i * 4;
			if (c.Length >= 4) {
				_speciesPalette[off + 0] = c[0];
				_speciesPalette[off + 1] = c[1];
				_speciesPalette[off + 2] = c[2];
				_speciesPalette[off + 3] = c[3];
			} else {
				// Fallback to opaque black when species color is missing.
				_speciesPalette[off + 0] = 0;
				_speciesPalette[off + 1] = 0;
				_speciesPalette[off + 2] = 0;
				_speciesPalette[off + 3] = 255;
			}
		}

		// Publish new flattened palette for subscribers (e.g., renderer).
		// Invoke with a cloned copy so subscribers cannot mutate internal state.
		SpeciesPaletteChanged?.Invoke(GetSpeciesPalette());
	}

	/// <summary>
	/// Set the current selected species indices used by placement requests.
	/// This is a cheap copy operation; callers (UI) should update when selection changes.
	/// </summary>
	public void SetSelectedSpeciesIndices(int[] indices) {
		_selectedSpeciesIndices = indices ?? [];
	}

	/// <summary>
	/// Enqueue a lightweight placement request (no species chosen yet). The simulation will pick
	/// a species from the selected species snapshot when applying requests.
	/// </summary>
	public void EnqueuePlacementRequest(int x, int y) {
		var layerIndex = ActiveLayerIndex;
		if (layerIndex < 0 || layerIndex >= _layers.Count)
			return;
		var grid = _layers[layerIndex].Grid;
		if (x < 0 || x >= grid.Width || y < 0 || y >= grid.Height)
			return;
		lock (_pendingLock) {
			_placementRequests.Add((layerIndex, x, y));
		}
	}

	/// <summary>
	/// Apply any pending placements into the NEXT buffers. Caller must hold the simulation step lock.
	/// This ensures placements are considered by the upcoming swap and next tick.
	/// </summary>
	public void ApplyPendingPlacements() {
		(int Layer, int X, int Y, byte Species)[] explicitSnapshot;
		(int Layer, int X, int Y)[] requestSnapshot;

		explicitSnapshot = [.. _pendingPlacements];
		_pendingPlacements.Clear();

		requestSnapshot = [.. _placementRequests];
		_placementRequests.Clear();

		// Resolve lightweight requests into concrete placements using a snapshot of selected indices.
		int[] selectedSnapshot = _selectedSpeciesIndices.Length == 0 ? [] : (int[]) _selectedSpeciesIndices.Clone();
		var rnd = Random.Shared;
		var resolved = new List<(int Layer, int X, int Y, byte Species)>();

		if (requestSnapshot.Length > 0 && selectedSnapshot.Length > 0) {
			foreach (var (Layer, X, Y) in requestSnapshot) {
				int choice = selectedSnapshot[rnd.Next(selectedSnapshot.Length)];
				resolved.Add((Layer, X, Y, (byte) choice));
			}
		}

		// Combine explicit placements and resolved requests
		var all = new List<(int Layer, int X, int Y, byte Species)>();
		if (explicitSnapshot.Length > 0)
			all.AddRange(explicitSnapshot);
		if (resolved.Count > 0)
			all.AddRange(resolved);

		if (all.Count == 0)
			return;

		// Group by layer to minimize CopyCurrentToNext calls
		var byLayer = all.GroupBy(p => p.Layer);
		foreach (var g in byLayer) {
			int li = g.Key;
			if (li < 0 || li >= _layers.Count)
				continue;
			var grid = _layers[li].Grid;
			// Ensure next buffer prepared
			grid.CopyCurrentToNext();
			foreach (var p in g) {
				grid.SetNext(p.X, p.Y, p.Species);
			}
		}
	}

	/// <summary>
	/// Immediately set both CURRENT and NEXT buffers for instant visual feedback.
	/// Caller should hold appropriate locks if stepping concurrently.
	/// </summary>
	public void PlaceImmediate(int layerIndex, int x, int y, byte speciesValue) {
		if (layerIndex < 0 || layerIndex >= _layers.Count)
			return;
		var grid = _layers[layerIndex].Grid;
		if (x < 0 || x >= grid.Width || y < 0 || y >= grid.Height)
			return;
		lock (_pendingLock) {
			// Ensure both buffers are consistent then write
			grid.CopyCurrentToNext();
			grid.SetNext(x, y, speciesValue);
			grid.SetCurrent(x, y, speciesValue);
			// Also enqueue so the placement is reapplied after rules processing to avoid overwrites
			_pendingPlacements.Add((layerIndex, x, y, speciesValue));
		}
	}

	/// <summary>
	/// Place a species value into the specified layer's NEXT buffer at (x,y).
	/// This does not swap buffers; caller is responsible for synchronization and when buffers are swapped.
	/// </summary>
	public void PlaceSpeciesNext(int layerIndex, int x, int y, byte speciesValue) {
		if (layerIndex < 0 || layerIndex >= _layers.Count)
			return;
		var grid = _layers[layerIndex].Grid;
		if (x < 0 || x >= grid.Width || y < 0 || y >= grid.Height)
			return;
		// Ensure next buffer exists and is a copy of current so single-cell write is safe
		grid.CopyCurrentToNext();
		grid.SetNext(x, y, speciesValue);
	}
}
