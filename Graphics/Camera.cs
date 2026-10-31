using Biome2.Input;
using OpenTK.Mathematics;

namespace Biome2.Graphics;

/// <summary>
/// Simple 2D camera that supports pan, zoom, and z-axis rotation.
/// World coordinates are in float units, independent of cells.
/// </summary>
public sealed class Camera(int viewportWidth, int viewportHeight) {
	private const float MaxClosenessZoomFactor = 300.0f;
	private const float ZoomScrollWheelFactor = 0.2f;
	private const float RotationSensitivity = 0.005f;

	private int _viewportWidth = viewportWidth;
	private int _viewportHeight = viewportHeight;

	private Vector2 _position = Vector2.Zero;
	private float _rotation = 0.0f;

	private float _zoom = 1.0f;
	private float _minFitZoom = 1.0f;


	public void Resize(int width, int height) {
		_viewportWidth = width;
		_viewportHeight = height;
	}

	public void FrameWorld(float worldWidth, float worldHeight) {
		// Fit world into viewport.
		var scaleX = _viewportWidth / worldWidth;
		var scaleY = _viewportHeight / worldHeight;
		_zoom = MathF.Min(scaleX, scaleY) * 0.9f;
		_minFitZoom = _zoom * 0.25f;

		_position = new Vector2(worldWidth * 0.5f, worldHeight * 0.5f);

		_rotation = 0.0f;
	}

	public void PanBy(Vector2 screenDelta) {
		var worldDelta = RotateToWorld(ApplyZoomFactor(screenDelta));
		_position -= worldDelta;
	}

	public void RotateBy(float deltaRadians) {
		_rotation += deltaRadians * RotationSensitivity;
		// Normalize rotation to [-PI, PI]
		while (_rotation > MathF.PI)
			_rotation -= MathF.Tau;
		while (_rotation < -MathF.PI)
			_rotation += MathF.Tau;
	}

	public float GetZoom() {
		return _zoom;
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
			var worldBefore = ScreenToWorld(new Vector2(input.MouseX, input.MouseY), _zoom);

			var zoomFactor = 1.0f + (input.MouseWheelDelta * ZoomScrollWheelFactor);
			_zoom = Math.Clamp(_zoom * zoomFactor, _minFitZoom, MaxClosenessZoomFactor);

			// World position under the cursor after zoom (with unchanged Position)
			var worldAfter = ScreenToWorld(new Vector2(input.MouseX, input.MouseY), _zoom);

			// Move camera so the world point under the cursor remains fixed on screen.
			_position += worldBefore - worldAfter;
		}

        // Right mouse panning is now handled by InputState.HandleInteractions.
	}

	public Matrix4 GetViewProjection() {
		// Orthographic projection in world units.
		// The camera Position is centered.
		var halfW = ApplyZoomFactor(_viewportWidth * 0.5f);
		var halfH = ApplyZoomFactor(_viewportHeight * 0.5f);

		var left = _position.X - halfW;
		var right = _position.X + halfW;
		var bottom = _position.Y - halfH;
		var top = _position.Y + halfH;

		var projection = Matrix4.CreateOrthographicOffCenter(left, right, bottom, top, -1.0f, 1.0f);
		
		// Apply rotation around the camera center (Z-axis rotation)
		if (MathF.Abs(_rotation) > 0.0001f) {
			var translate1 = Matrix4.CreateTranslation(-_position.X, -_position.Y, 0);
			var rotate = Matrix4.CreateRotationZ(-_rotation);
			var translate2 = Matrix4.CreateTranslation(_position.X, _position.Y, 0);
			projection = translate1 * rotate * translate2 * projection;
		}
		
		return projection;
	}

    public Vector2 ScreenToWorld(Vector2 screenPos, float zoomOverride) {
		// Screen origin is top-left with Y down (from InputState). Convert to NDC-style world coords.
		var halfW = ApplyZoomFactor(_viewportWidth * 0.5f, zoomOverride);
		var halfH = ApplyZoomFactor(_viewportHeight * 0.5f, zoomOverride);

		// Calculate world bounds
		var left = _position.X - halfW;
		var bottom = _position.Y - halfH;

		// InputState.MouseY is in window coords with origin top-left; invert Y to match world with bottom-left origin
		var sx = screenPos.X;
		var sy = screenPos.Y;

		// Convert screen (pixels) to world
		var worldX = left + (sx / _viewportWidth) * (halfW * 2.0f);
		var worldY = bottom + ((_viewportHeight - sy) / _viewportHeight) * (halfH * 2.0f);
		var worldPos = _position + RotateToWorld(new Vector2(worldX, worldY) - _position);

		return worldPos;
	}

    private float ApplyZoomFactor(float value, float overrideZoom = -1) {
        var denom = MathF.Max(overrideZoom >= 0 ? overrideZoom : _zoom, 0.001f);
        return value / denom;
    }

    private Vector2 ApplyZoomFactor(Vector2 value, float overrideZoom = -1) {
        var denom = MathF.Max(overrideZoom >= 0 ? overrideZoom : _zoom, 0.001f);
        return value / denom;
    }


	// Rotates a camera-aligned vector (screen axes, world scale) into world axes.
	private Vector2 RotateToWorld(Vector2 v) {
		if (MathF.Abs(_rotation) <= 0.0001f)
			return v;

		var cos = MathF.Cos(_rotation);
		var sin = MathF.Sin(_rotation);
		return new Vector2(
			v.X * cos - v.Y * sin,
			v.X * sin + v.Y * cos);
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
			move = RotateToWorld(move.Normalized());
			_position += move * speed * dt;
		}
	}
}
