using Biome2.Diagnostics;
using Biome2.Graphics;
using Biome2.Input;
using Biome2.Simulation;
using Biome2.World;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace Biome2.App;

/// <summary>
/// Owns the window, render loop, input polling, and top level orchestration.
/// Keep this class thin. Most logic should live in subsystem classes.
/// </summary>
public sealed class BiomeApp : GameWindow {
	private readonly AppConfig _config;

	private readonly Performance _perf = new();

	private InputState _input;

	private ImGuiController _ui;

	private Renderer _renderer = null!;
	private Camera _camera = null!;

    private WorldState _world = null!;
	private SimulationController _simulation = null!;

	public BiomeApp(AppConfig config)
		: base(GameWindowSettings.Default,
			new NativeWindowSettings {
				Title = config.WindowTitle,
				ClientSize = new Vector2i(config.WindowWidth, config.WindowHeight),
				Flags = ContextFlags.ForwardCompatible,
				APIVersion = new Version(4, 5),
				Profile = ContextProfile.Core
			}) {
		_config = config;

		// Initialize ImGui UI controller
		_ui = new ImGuiController(this);
		_input = new InputState();
	}

	protected override void OnLoad() {
		base.OnLoad();

		VSync = _config.VSyncEnabled ? VSyncMode.On : VSyncMode.Off;

		_camera = new Camera(Size.X, Size.Y);

        _world = WorldState.CreateBlank();

        _simulation = new SimulationController(_world, _perf);

		// Listen for world replacement so subsystems (renderer, camera) can update.
		_simulation.WorldReplaced += OnWorldReplaced;

		_renderer = new Renderer(_config.CellSize);
		_renderer.Initialize();
		_renderer.SetWorld(_world);

		// Start with the camera showing the full world.
		_camera.FrameWorld(
			worldWidth: _world.WidthCells * _config.CellSize,
			worldHeight: _world.HeightCells * _config.CellSize);

		Logger.Info("App loaded.");
	}

    private void OnWorldReplaced(WorldState newWorld) {
		// Update local reference and notify renderer and camera.
		_world = newWorld;
		_renderer.SetWorld(_world);
		_camera.FrameWorld(
			worldWidth: _world.WidthCells * _config.CellSize,
			worldHeight: _world.HeightCells * _config.CellSize);
	}

	protected override void OnResize(ResizeEventArgs e) {
		base.OnResize(e);
		_camera.Resize(e.Width, e.Height);
		Renderer.Resize(e.Width, e.Height);
	}

	protected override void OnUpdateFrame(FrameEventArgs args) {
		base.OnUpdateFrame(args);

		_perf.BeginUpdate(args.Time);

		_input.UpdateFrom(this);

		_input.ProcessInputs(_camera, _world);

		// Update UI input state so it can request capture of mouse/keyboard.
		_ui.UpdateInputState(_input);

		// Camera movement now, UI can override input capture.
		_camera.Update(_input, (float) args.Time);

		// Simulation currently runs only when enabled.
		_simulation.Update((float) args.Time);

		_perf.EndUpdate();
	}

	protected override void OnRenderFrame(FrameEventArgs args) {
		base.OnRenderFrame(args);

		_perf.BeginRender(args.Time);

		// handle interactions like panning and placement (uses camera, renderer, simulation)
		_input.HandleInteractions(_camera, _renderer, _simulation);

		// If drawing is enabled, render every frame. If disabled, only render when
		// an interaction occurred that should update the view (panning/placement).
		if (_renderer.DrawingEnabled || _input.HadInteraction) {
			_renderer.Render(_camera);
			// Draw placement highlight overlay (based on input)
			_renderer.DrawHighlight(_camera, _input);
		}

        // Render UI on top of world
        _ui.RenderUI(_renderer, _simulation, _input, _camera);

		SwapBuffers();

		_perf.EndRender();
	}

	protected override void OnUnload() {
		base.OnUnload();
		// Unsubscribe from simulation events if subscribed
		if (_simulation != null) {
			try {
				_simulation.WorldReplaced -= OnWorldReplaced;
			} catch {
				// ignore if already unsubscribed
			}
		}

		// Dispose subsystems if they exist. Use null-conditional to be defensive
		_renderer?.Dispose();
		_simulation?.Dispose();
		_ui?.Dispose();

		// Persist TPS stats (log any error rather than silently swallowing)
		try {
			_perf.SaveMaxTps(_simulation?.LastLoadedRulesFilePath);
		} catch (Exception ex) {
			Logger.Warn($"Failed saving TPS stats: {ex.Message}");
		}

		Logger.Info("App unloaded.");
	}
}

