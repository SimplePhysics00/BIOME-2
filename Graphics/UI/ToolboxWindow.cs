using ImGuiNET;
using System.Numerics;

namespace Biome2.Graphics.UI;

internal sealed class ToolboxWindow
{
    private const string Title = "Toolbox";

    private static ImGuiWindowFlags GetWindowFlags()
    {
        return ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBringToFrontOnFocus;
    }

    public void Render(Renderer renderer, Simulation.SimulationController simulation)
    {
        // Force the UI to a fixed screen location and prevent it from being moved by dragging.
        // Use 0,0 so the UI is positioned at the top-left of the application viewport.
        ImGui.SetNextWindowPos(new Vector2(0, 0), ImGuiCond.Always);

        // AlwaysAutoResize will size to content, but enforce size constraints
        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 90), new Vector2(float.MaxValue, float.MaxValue), null);

        ImGui.Begin(Title, GetWindowFlags());

        // Pause toggle
        bool paused = simulation.Clock.Paused;
        if (ImGui.Checkbox("Paused", ref paused))
        {
            simulation.Clock.Paused = paused;
        }

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

		ImGui.End();
	}
}
