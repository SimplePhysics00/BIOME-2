using Biome2.Input;
using OpenTK.Mathematics;

namespace Biome2.Graphics;

/// <summary>
/// Simple 2D camera that supports pan and zoom.
/// World coordinates are in float units, independent of cells.
/// </summary>
public sealed class Camera {
	public int ViewportWidth { get; private set; }
	public int ViewportHeight { get; private set; }

	public Vector2 Position { get; private set; } = Vector2.Zero;
	public float Zoom { get; private set; } = 1.0f;
	private float MinFitZoom = 1.0f;

    // Right mouse drag state for panning with a small movement threshold.
	//private const float MinFarnessZoomFactor = 1.0f;
	private const float MaxClosenessZoomFactor = 300.0f;
	private const float ZoomScrollWheelFactor = 0.2f;

	public Camera(int viewportWidth, int viewportHeight) {
		ViewportWidth = viewportWidth;
		ViewportHeight = viewportHeight;
	}

	public void Resize(int width, int height) {
		ViewportWidth = width;
		ViewportHeight = height;
	}

	public void FrameWorld(float worldWidth, float worldHeight) {
		// Fit world into viewport.
		var scaleX = ViewportWidth / worldWidth;
		var scaleY = ViewportHeight / worldHeight;
		MinFitZoom = MathF.Min(scaleX, scaleY) * 0.9f;
		Zoom = MinFitZoom;

		Position = new Vector2(worldWidth * 0.5f, worldHeight * 0.5f);
	}

	public void Update(InputState input, float dt) {
		// If UI wants the mouse, don't allow camera panning or zooming from mouse.
		if (input.GuiWantsMouse) {
			return;
		}

		HandleKeyboardMovement(input, dt);

		// Mouse wheel zoom, zooms around cursor position.
		if (MathF.Abs(input.MouseWheelDelta) > 0.0f) {
			// World position under the cursor before zoom
			var worldBefore = ScreenToWorld(new Vector2(input.MouseX, input.MouseY), Zoom);

			var zoomFactor = 1.0f + (input.MouseWheelDelta * ZoomScrollWheelFactor);
			Zoom = Math.Clamp(Zoom * zoomFactor, MinFitZoom, MaxClosenessZoomFactor);

			// World position under the cursor after zoom (with unchanged Position)
			var worldAfter = ScreenToWorld(new Vector2(input.MouseX, input.MouseY), Zoom);

			// Move camera so the world point under the cursor remains fixed on screen.
			Position += worldBefore - worldAfter;
		}

        // Right mouse panning is now handled by InputState.HandleInteractions.
	}

	private void HandleKeyboardMovement(InputState input, float dt) {
		// Pan speed changes with zoom so it feels stable.
		var speed = ApplyZoomFactor(600.0f);
		if (input.KeyShift)
			speed *= 2.0f;

		var move = Vector2.Zero;
		if (input.KeyW)
			move.Y += 1.0f;
		if (input.KeyS)
			move.Y -= 1.0f;
		if (input.KeyA)
			move.X -= 1.0f;
		if (input.KeyD)
			move.X += 1.0f;

		if (move.LengthSquared > 0.0f) {
			move = move.Normalized();
			Position += move * speed * dt;
		}
	}

	public Matrix4 GetViewProjection() {
		// Orthographic projection in world units.
		// The camera Position is centered.
		var halfW = ApplyZoomFactor(ViewportWidth * 0.5f);
		var halfH = ApplyZoomFactor(ViewportHeight * 0.5f);

		var left = Position.X - halfW;
		var right = Position.X + halfW;
		var bottom = Position.Y - halfH;
		var top = Position.Y + halfH;

		var projection = Matrix4.CreateOrthographicOffCenter(left, right, bottom, top, -1.0f, 1.0f);
		return projection;
	}

    public Vector2 ScreenToWorld(Vector2 screenPos, float zoomOverride) {
		// Screen origin is top-left with Y down (from InputState). Convert to NDC-style world coords.
		var halfW = ApplyZoomFactor(ViewportWidth * 0.5f, zoomOverride);
		var halfH = ApplyZoomFactor(ViewportHeight * 0.5f, zoomOverride);

		// Calculate world bounds
		var left = Position.X - halfW;
		var bottom = Position.Y - halfH;

		// InputState.MouseY is in window coords with origin top-left; invert Y to match world with bottom-left origin
		var sx = screenPos.X;
		var sy = screenPos.Y;

		// Convert screen (pixels) to world
		var worldX = left + (sx / ViewportWidth) * (halfW * 2.0f);
		var worldY = bottom + ((ViewportHeight - sy) / ViewportHeight) * (halfH * 2.0f);

		return new Vector2(worldX, worldY);
	}

    private T ApplyZoomFactor<T>(T value, float overrideZoom = -1) where T : struct {
        // works for both float and Vector2 (OpenTK.Mathematics.Vector2) without duplicating logic.
        var denom = MathF.Max(overrideZoom >= 0 ? overrideZoom : Zoom, 0.001f);
        return (T)((dynamic)value / denom);
    }

    // Public helper to pan the camera by a screen-space delta that is adjusted by zoom.
    public void PanBy(Vector2 screenDelta) {
        Position -= ApplyZoomFactor(screenDelta);
    }
}
