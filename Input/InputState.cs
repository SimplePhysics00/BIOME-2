using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Mathematics;
using static Biome2.Input.PlacementModes;
using Biome2.Graphics;
using Biome2.World;
using Biome2.World.CellGrid;

namespace Biome2.Input;

/// <summary>
/// Centralized, frame coherent input snapshot.
/// Later, ImGui can mark when it wants to capture mouse and keyboard input.
/// </summary>
public sealed class InputState {
	public float MouseX { get; private set; }
	public float MouseY { get; private set; }
	public float MouseDeltaX { get; private set; }
	public float MouseDeltaY { get; private set; }
	public float MouseWheelDelta { get; private set; }

	public bool MouseLeftDown { get; private set; }
	public bool MouseRightDown { get; private set; }
	public bool MouseMiddleDown { get; private set; }

	public bool KeyW { get; private set; }
	public bool KeyA { get; private set; }
	public bool KeyS { get; private set; }
	public bool KeyD { get; private set; }
	public bool KeyQ { get; private set; }

	public bool KeyShift { get; private set; }
	public bool KeyCtrl { get; private set; }

	// Signals from ImGui whether it wants to capture input. App should honor these to block world interaction.
	public bool GuiWantsMouse { get; private set; }
	public bool GuiWantsKeyboard { get; private set; }

	private float _lastMouseX;
	private float _lastMouseY;

	private PlacementMode _placementMode = PlacementMode.PIXEL;

	// Right-drag state for panning
	private bool _rightMouseWasDown;
    private bool _rightDragStarted;
    private Vector2 _rightDragStart;
    
	// Middle-drag state for rotation
	private bool _middleMouseWasDown;
	private bool _middleDragStarted;
	private Vector2 _middleDragStart;
    
    // Flag set when an interaction that should trigger a visual update occurred
    public bool HadInteraction { get; private set; }

    // Placement state for left-button painting
    private bool _placing;
    private int _lastPlacedX = -1;
    private int _lastPlacedY = -1;

    // Snapshot of selected species indices for painting
    private int[] _selectedSpecies = [];

    // Set snapshot of selected species indices (called from UI)
    public void SetSelectedSpeciesIndices(int[]? indices) {
        _selectedSpecies = indices ?? [];
    }

    public PlacementMode GetPlacementMode() => _placementMode;

    public bool IsPlacing() => _placing;

    public (int X, int Y) GetPlacementStart() => (_lastPlacedX, _lastPlacedY);

    // Compute hovered cell coordinates given camera and renderer
    public (int X, int Y) GetHoverCell(Camera camera, Renderer renderer) {
        var worldPos = camera.ScreenToWorld(new Vector2(MouseX, MouseY), camera.Zoom);
        float cs = renderer.CellSize;
        // If renderer/world indicate a spiral topology, ask the DiskCellGrid to map world coords -> (ring,pos).
        var world = renderer.World;

		if (world.ActiveLayer?.Grid is DiskCellGrid disk) {
			var (X, Y) = disk.MapWorldToCell(worldPos, cs);
			return (X, Y);
		} else if (world.ActiveLayer?.Grid is HexCellGrid hex) {
			var (X, Y) = hex.MapWorldToCell(worldPos, cs);
			return (X, Y);
		} else {
            int cellX = (int) Math.Floor(worldPos.X / cs);
            int cellY = (int) Math.Floor(worldPos.Y / cs);
            return (cellX, cellY);
		}
	}
	
    public void SetPlacementMode(PlacementMode mode) => _placementMode = mode;

	// Central entry for handling interactions that involve camera panning and painting.
	public void HandleInteractions(Camera camera, Renderer renderer, Simulation.SimulationController simulation) {
        // Reset per-frame interaction flag. It will be set if an action occurred
        // that should cause a visual update while drawing is disabled.
        HadInteraction = false;

        // If UI wants the mouse, do nothing.
        if (GuiWantsMouse) {
            // reset drag/placing states to avoid stale state
            _rightMouseWasDown = false;
            _rightDragStarted = false;
            _middleMouseWasDown = false;
            _middleDragStarted = false;
            EndPlacement();
            return;
        }

        // Right-button panning
        if (MouseRightDown) {
            if (!_rightMouseWasDown) {
                _rightMouseWasDown = true;
                _rightDragStarted = false;
                _rightDragStart = new Vector2(MouseX, MouseY);
            } else {
                if (!_rightDragStarted) {
                    var dx = MouseX - _rightDragStart.X;
                    var dy = MouseY - _rightDragStart.Y;
                    if (dx * dx + dy * dy > 4.0f * 4.0f) {
                        _rightDragStarted = true;
                        var drag = new Vector2(MouseDeltaX, -MouseDeltaY);
                        camera.PanBy(drag);
                        HadInteraction = true;
                    }
                } else {
                    var drag = new Vector2(MouseDeltaX, -MouseDeltaY);
                    camera.PanBy(drag);
                    HadInteraction = true;
                }
            }
        } else {
            _rightMouseWasDown = false;
            _rightDragStarted = false;
        }

        // Middle-button rotation
        if (MouseMiddleDown) {
            if (!_middleMouseWasDown) {
                _middleMouseWasDown = true;
                _middleDragStarted = false;
                _middleDragStart = new Vector2(MouseX, MouseY);
            } else {
                if (!_middleDragStarted) {
                    var dx = MouseX - _middleDragStart.X;
                    var dy = MouseY - _middleDragStart.Y;
                    if (dx * dx + dy * dy > 4.0f * 4.0f) {
                        _middleDragStarted = true;
                        camera.RotateBy(MouseDeltaX);
                        HadInteraction = true;
                    }
                } else {
                    camera.RotateBy(MouseDeltaX);
                    HadInteraction = true;
                }
            }
        } else {
            _middleMouseWasDown = false;
            _middleDragStarted = false;
        }

        // Left-button painting
        if (MouseLeftDown) {
            HandlePlacement(camera, renderer, simulation);
        } else {
            // If we were placing in Zone mode, finalize the rectangle on release
            if (_placing && _placementMode == PlacementMode.ZONE) {
                FinalizeZonePlacement(camera, renderer, simulation);
            }

            EndPlacement();
        }
    }

    // Handle placement action: picks cell under cursor and enqueues request if cell changed.
    public void HandlePlacement(Camera camera, Renderer renderer, Simulation.SimulationController simulation) {
        // Do not act when UI wants mouse
        if (GuiWantsMouse) return;

        var worldPos = camera.ScreenToWorld(new Vector2(MouseX, MouseY), camera.Zoom);
        float cs = renderer.CellSize;
        int cellX, cellY;
        var world = renderer.World;

		if (world.ActiveLayer?.Grid is DiskCellGrid disk) {
			var (X, Y) = disk.MapWorldToCell(worldPos, cs);
			cellX = X;
			cellY = Y;
		} else if (world.ActiveLayer?.Grid is HexCellGrid hex) {
            var (X, Y) = hex.MapWorldToCell(worldPos, cs);
            cellX = X;
            cellY = Y;
        } else {
            cellX = (int) Math.Floor(worldPos.X / cs);
            cellY = (int) Math.Floor(worldPos.Y / cs);
        }

		if (_placementMode == PlacementMode.PIXEL) {
            if (!_placing || cellX != _lastPlacedX || cellY != _lastPlacedY) {
                // snapshot selected species indices
                var sel = _selectedSpecies;
                if (sel.Length == 0) return;

                simulation.SetSelectedSpeciesIndices(sel);
                simulation.EnqueuePlacementRequest(cellX, cellY);

                // Request immediate visual update (renderer uses world's current buffer)
                renderer.UploadSingleCell(simulation.World.ActiveLayer.Grid, cellX, cellY);

                // Indicate we performed an interaction that should force a render while drawing disabled
                HadInteraction = true;

				_lastPlacedX = cellX;
                _lastPlacedY = cellY;
                _placing = true;
            }
        } else {
            // Zone mode: on initial press record start coordinates, do not enqueue yet
            if (!_placing) {
                _lastPlacedX = cellX;
                _lastPlacedY = cellY;
                _placing = true;
            }
            // optional: could provide live preview here
        }
    }

    private void FinalizeZonePlacement(Camera camera, Renderer renderer, Simulation.SimulationController simulation) {
        // compute end cell from current mouse
        var worldPos = camera.ScreenToWorld(new Vector2(MouseX, MouseY), camera.Zoom);
        float cs = renderer.CellSize;
        int endX, endY;
        var world = renderer.World;

		switch (world.GridTopology) {
			case GridTopology.SPIRAL when world.ActiveLayer?.Grid is DiskCellGrid disk: {
				var (X, Y) = disk.MapWorldToCell(worldPos, cs);
				endX = X;
				endY = Y;
				break;
			}

			case GridTopology.HEX when world.ActiveLayer?.Grid is HexCellGrid hex: {
				var (X, Y) = hex.MapWorldToCell(worldPos, cs);
				endX = X;
				endY = Y;
				break;
			}

			default:
				endX = (int) Math.Floor(worldPos.X / cs);
				endY = (int) Math.Floor(worldPos.Y / cs);
				break;
		}

		int startX = _lastPlacedX;
        int startY = _lastPlacedY;
        if (startX < 0 || startY < 0) return;

        int minX = Math.Min(startX, endX);
        int maxX = Math.Max(startX, endX);
        int minY = Math.Min(startY, endY);
        int maxY = Math.Max(startY, endY);

        var sel = _selectedSpecies;
        if (sel.Length == 0) return;

        // Snapshot selection once
        simulation.SetSelectedSpeciesIndices(sel);

        // Mark that we will update visuals as a result of this interaction
        HadInteraction = true;

        for (int y = minY; y <= maxY; y++) {
            for (int x = minX; x <= maxX; x++) {
                simulation.EnqueuePlacementRequest(x, y);
            }
        }
    }


	// End active placement (called when left button released)
	public void EndPlacement() {
		_placing = false;
		_lastPlacedX = -1;
		_lastPlacedY = -1;
	}

	public void UpdateFrom(GameWindow window) {
		var mouse = window.MouseState;
		var keyboard = window.KeyboardState;

		MouseX = mouse.X;
		MouseY = mouse.Y;

		MouseDeltaX = MouseX - _lastMouseX;
		MouseDeltaY = MouseY - _lastMouseY;

		_lastMouseX = MouseX;
		_lastMouseY = MouseY;

		// OpenTK exposes scroll delta since last poll.
		MouseWheelDelta = mouse.ScrollDelta.Y;

		MouseLeftDown = mouse.IsButtonDown(MouseButton.Left);
		MouseRightDown = mouse.IsButtonDown(MouseButton.Right);
		MouseMiddleDown = mouse.IsButtonDown(MouseButton.Middle);

        KeyW = keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.W);
        KeyA = keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.A);
        KeyS = keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.S);
        KeyD = keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.D);
        
        KeyQ = keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Q);

        KeyShift = keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.LeftShift) || keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.RightShift);
        KeyCtrl = keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.LeftControl) || keyboard.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.RightControl);
	}

	/// <summary>
	/// Method to unify world and camera within inputs so that we can update world
	/// settings and camera state with keybinds (TODO: could move WASD controls here).
	/// </summary>
	public void ProcessInputs(Camera camera, WorldState world) {
        if (KeyQ)
            camera.FrameWorld(world.WidthCells, world.HeightCells);

	}

	// Called by ImGuiController to set capture flags.
	public void SetGuiWants(bool mouse, bool keyboard) {
		GuiWantsMouse = mouse;
		GuiWantsKeyboard = keyboard;
	}
}
