using Biome2.Diagnostics;
using Biome2.Simulation;
using Biome2.World;
using ImGuiNET;
using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using static Biome2.Input.InputState;
using static System.ComponentModel.Design.ObjectSelectorEditor;

namespace Biome2.Graphics.UI;

internal sealed class ToolboxWindow
{
    private const string Title = "Toolbox";
	private const string LoadRulesButtonLabel = "Load Rules";
	private const string DebugRulesButtonLabel = "Debug Rules";
	private const string RestartWorldButtonLabel = "Restart World";

	// Stored delay values to avoid recalculating every frame. They are updated
	// only when the user moves the slider.
	// Defaults: delay = 0, slider position = 0.
	private float CurrentDelay { get; set; } = 0.0f;
    private int DelayMs { get; set; } = 0;
    // Cached slider position.
    private float DelaySliderPos { get; set; } = 0.0f;

    private float _windowWidth;
    // Per-species selection for placement
    private bool[]? _selectedSpecies;
    // Editable grid size fields (spinners)
    private int _gridWidth = 0;
    private int _gridHeight = 0;
    private WorldState? _lastWorldRef = null;

    private static ImGuiWindowFlags GetWindowFlags()
    {
        return ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
			| ImGuiWindowFlags.NoSavedSettings
			| ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBringToFrontOnFocus;
    }

    public void Render(Renderer renderer, SimulationController simulation, Input.InputState input)
    {
        // Force the UI to a fixed screen location and prevent it from being moved by dragging.
        // Use 0,0 so the UI is positioned at the top-left of the application viewport.
        ImGui.SetNextWindowPos(new Vector2(0, 0), ImGuiCond.Always);

        // AlwaysAutoResize will size to content, but enforce size constraints
        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 90), new Vector2(float.MaxValue, float.MaxValue), null);

		var style = ImGui.GetStyle();
		var primaryColor = style.Colors[(int) ImGuiCol.TitleBgActive];
		// Force unfocused/collapsed title to use the same blue as the active title
		ImGui.PushStyleColor(ImGuiCol.TitleBg, primaryColor);
		ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, primaryColor);
		ImGui.Begin(Title, GetWindowFlags());
		ImGui.PopStyleColor(2);

		// ensure data is populated from the current loaded world
		var world = simulation.World;
		if (world != null) {
			// If the world instance changed since last frame, update fields to match
			if (!object.ReferenceEquals(_lastWorldRef, world)) {
				_gridWidth = world.WidthCells;
				_gridHeight = world.HeightCells;
				_lastWorldRef = world;
			}
		}

		// Load Rules button (centered)
		{
            var textSize = ImGui.CalcTextSize(LoadRulesButtonLabel);
            var framePadding = ImGui.GetStyle().FramePadding;
            var buttonWidth = textSize.X + framePadding.X * 2.0f;
            ImGui.SetCursorPosX((_windowWidth - buttonWidth) * 0.5f);
        }
        if (ImGui.Button(LoadRulesButtonLabel)) {
			simulation.Clock.Paused = true;
            // Attempt to show a native file dialog per-platform.
            string? selected = null;

            try {
                // Use WinForms OpenFileDialog directly since app now targets Windows.
                try {
                    using var ofd = new System.Windows.Forms.OpenFileDialog();
                    ofd.Filter = "Rules files|*.bio;*.txt|All files|*.*";
                    ofd.Multiselect = false;
                    // Start dialog in the directory where the executable resides.
                    try {
                        ofd.InitialDirectory = AppContext.BaseDirectory;
                    } catch {
                        // ignore if unable to set
                    }
                    var res = ofd.ShowDialog();
                    if (res == System.Windows.Forms.DialogResult.OK) selected = ofd.FileName;
                } catch (Exception ex) {
                    Logger.Error($"Windows file dialog failed: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(selected) && File.Exists(selected)) {
                    var loader = new FileLoading.RulesLoader();
                    var request = FileLoading.RulesLoader.Load(selected);

                    // Apply to simulation controller via new ApplyRules API
                    simulation.ApplyRules(request);
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to load rules file: {ex.Message}");
            }
		}

		// Pause toggle
		bool paused = simulation.Clock.Paused;
        if (ImGui.Checkbox("Paused", ref paused)) {
            simulation.Clock.Paused = paused;
        }

		// Delay time slider: use a nonlinear curve so mid slider values yield small millisecond delays.
		// Mapping: DelayTime (seconds) = slider^expo * 1.0 (max 1s). Inverse used to position the slider.
		const int DelaySliderExponent = 4;
        string tickDelayText;
        if (CurrentDelay > 0.0f && DelayMs < 1)
            tickDelayText = $"Tick Delay: ~{DelayMs} ms";
        else
            tickDelayText = $"Tick Delay: {DelayMs} ms";

		ImGui.Text(tickDelayText);
		// Use cached slider position; it defaults to 0 and is updated only when
		// the user moves the control.
		float sliderPos = DelaySliderPos;
		ImGui.PushItemWidth(-1);
		if (ImGui.SliderFloat("##TickDelayTime", ref sliderPos, 0.0f, 1.0f)) {
			// store new slider position
			DelaySliderPos = sliderPos;
			float newDelay = MathF.Pow(Math.Clamp(sliderPos, 0.0f, 1.0f), DelaySliderExponent);
			simulation.Clock.DelayTime = newDelay;
			CurrentDelay = newDelay;
			DelayMs = (int) Math.Round(CurrentDelay * 1000.0f);
		}
		ImGui.PopItemWidth();

		ImGui.Separator();

        // Compute a dynamic child height. Using GetContentRegionAvail() alone can be
        // misleading when the window is AlwaysAutoResize, so base the available
        // vertical space on the display size and window position so the child can
        // expand when the app has more screen real-estate (e.g., fullscreen/4k).
        var io = ImGui.GetIO();
        var winPos = ImGui.GetWindowPos();
        // Reserve some pixels at the bottom of the display and for controls below the child
        float screenBottomMargin = 20.0f;
        float reservedBelow = 255.0f; // space to leave for bottom controls inside the window
        float screenAvailBelow = Math.Max(reservedBelow, io.DisplaySize.Y - winPos.Y - screenBottomMargin);
        float childHeight = Math.Max(reservedBelow, screenAvailBelow - reservedBelow);
        ImGui.BeginChild("##ScrollableArea", new Vector2(-1, childHeight));

		// ShowGrid toggle
		bool showGrid = renderer.ShowGrid;
		if (ImGui.Checkbox("Show Grid", ref showGrid)) {
			renderer.ShowGrid = showGrid;
		}

		// Show Axes toggle
		bool showAxes = renderer.ShowAxes;
		if (ImGui.Checkbox("Show Axes", ref showAxes)) {
			renderer.ShowAxes = showAxes;
		}

		// Disable Drawing toggle: when enabled, suspends renderer drawing (UI still updates)
		bool drawingEnabled = renderer.DrawingEnabled;
		if (ImGui.Checkbox("Enable Drawing", ref drawingEnabled)) {
			renderer.DrawingEnabled = drawingEnabled;
		}

		ImGui.Separator();

		// Two input spinners for width and height
		ImGui.PushItemWidth(120);
		ImGui.InputInt("Width", ref _gridWidth, 1, 10);
		ImGui.InputInt("Height", ref _gridHeight, 1, 10);
		ImGui.PopItemWidth();

		// Restart World button (centered)
		{
			var textSize = ImGui.CalcTextSize(RestartWorldButtonLabel);
			var framePadding = ImGui.GetStyle().FramePadding;
			var buttonWidth = textSize.X + framePadding.X * 2.0f;
			ImGui.SetCursorPosX((_windowWidth - buttonWidth) * 0.5f);
		}
		if (ImGui.Button(RestartWorldButtonLabel)) {
			try {
				int w = Math.Max(1, _gridWidth);
				int h = Math.Max(1, _gridHeight);
				simulation.RestartWorld(w, h);
			} catch (Exception ex) {
				Logger.Error($"Failed to restart world: {ex.Message}");
			}
		}

		ImGui.Separator();

		// Layer selection (radio buttons) - reflect and set the world's active layer
		if (world != null) {
			ImGui.Text("Active Layer:");
			int activeIdx = world.ActiveLayerIndex;
			var layerNames = world.LayerNames;
			for (int i = 0; i < layerNames.Count; i++) {
				// Use a unique id suffix so visible labels can be duplicated safely.
				string label = $"{layerNames[i]}##layer_{i}";
				if (ImGui.RadioButton(label, ref activeIdx, i)) {
					world.ActiveLayerIndex = activeIdx;
				}
			}
		}

        ImGui.Separator();

		ImGui.Text("Paint Mode:");

		// Paint mode radio buttons (Pixel / Zone)
		int paintMode = (int)input.GetPlacementMode();
		if (ImGui.RadioButton("Pixel##paintmode", ref paintMode, (int)PlacementMode.Pixel)) {
			input.SetPlacementMode(PlacementMode.Pixel);
		}
		ImGui.SameLine();
		if (ImGui.RadioButton("Zone##paintmode", ref paintMode, (int)PlacementMode.Zone)) {
			input.SetPlacementMode(PlacementMode.Zone);
		}

		// Species selection: list species with checkboxes to enable manual placement
		if (world != null) {
            ImGui.Text("Species:");
            int speciesCount = world.Species.Count;
            // Maintain a static selection array per-window
            // Use ImGui storage via IDs is complex; keep a simple static list sized to speciesCount
            if (_selectedSpecies == null || _selectedSpecies.Length != Math.Max(1, speciesCount)) {
                _selectedSpecies = new bool[Math.Max(1, speciesCount)];
            }

            // Draw each species checkbox
            for (int si = 0; si < speciesCount; si++) {
                string label = $"{world.GetSpeciesName(si)}##spec_{si}";
                bool val = _selectedSpecies[si];
                if (ImGui.Checkbox(label, ref val)) {
                    _selectedSpecies[si] = val;
                }
            }

			// Convert selected array into indices and hand off to input once (cheap copy)
			var selected = new List<int>();
			for (int si = 0; si < speciesCount; si++)
				if (_selectedSpecies[si])
					selected.Add(si);

			if (selected.Count > 0) {
				input.SetSelectedSpeciesIndices([.. selected]);
			}
        }

		ImGui.EndChild();

		ImGui.Separator();

        // Debug Rules button: reconstruct a readable rule line from the simulation's stored rules
        {
            var textSize = ImGui.CalcTextSize(DebugRulesButtonLabel);
            var framePadding = ImGui.GetStyle().FramePadding;
            var buttonWidth = textSize.X + framePadding.X * 2.0f;
            ImGui.SetCursorPosX((_windowWidth - buttonWidth) * 0.5f);
        }
        if (ImGui.Button(DebugRulesButtonLabel)) {
            var rules = simulation.Rules;
            if (rules == null || rules.Count == 0) {
                Logger.Info("Debug Rules: no rules loaded.");
            } else {
                Logger.Info("  ===== Rules =====");
                foreach (var r in rules) {
					r.ReportRuleDetails();
                }
                Logger.Info("  === End Rules ===");
            }
        }

		_windowWidth = ImGui.GetWindowWidth();

		ImGui.End();
	}
}
