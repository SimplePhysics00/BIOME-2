using Biome2.Diagnostics;
using Biome2.Graphics.GlObjects;
using Biome2.Graphics.GLObjects;
using Biome2.World;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Biome2.Graphics;

/// <summary>
/// Renders the active world layer as a grid of cells.
/// Uses instancing, one quad per cell, and colors by cell state.
/// Today everything is empty, later you will map values to palettes.
/// </summary>
public sealed class Renderer : IDisposable {
	private readonly float _cellSize;

	private ShaderProgram _shader = null!;
	private ShaderProgram _axisShader = null!;

	private VertexArrayObject _vao = null!;
	private BufferObject _quadVbo = null!;
	private BufferObject _instanceVbo = null!;
	private VertexArrayObject _axisVao = null!;
	private BufferObject _axisVbo = null!;

	private WorldModel _world = null!;

	private int _uViewProj;
	private int _uCellSize;
	private int _uGridSize;
	private int _uShowGrid;
	private int _uPixelsPerUnit;
	private int _uGridThicknessPx;
	private int _uCellColors;

	// Controls for rendering options
	public bool ShowGrid { get; set; } = false;
	public bool ShowAxes { get; set; } = true;

	public float GridThicknessPixels { get; set; } = 1.0f;

	private Vector2[] _instancePositions = Array.Empty<Vector2>();

	// Per-cell color texture (RGBA8). Each cell maps to one texel.
	private int _cellColorsTex = 0;

	// Cached flattened RGBA8 palette copied from WorldModel on species changes.
	// Length = speciesCount * 4. If empty then renderer should fall back to default color.
	private byte[] _speciesPalette = Array.Empty<byte>();

    // default fallback color when palette empty
	private static readonly byte[] _defaultFallbackColor = [255, 255, 255, 255];

	public Renderer(float cellSize) {
		_cellSize = cellSize;
	}

	public void Initialize() {
		GL.ClearColor(0.08f, 0.08f, 0.10f, 1.0f);
		GL.Enable(EnableCap.Blend);
		GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

		_shader = new ShaderProgram(Shaders.GridVertex, Shaders.GridFragment);

		_uViewProj = _shader.GetUniformLocation("uViewProj");
		_uCellSize = _shader.GetUniformLocation("uCellSize");
		_uGridSize = _shader.GetUniformLocation("uGridSize");
		_uShowGrid = _shader.GetUniformLocation("uShowGrid");
		_uPixelsPerUnit = _shader.GetUniformLocation("uPixelsPerUnit");
		_uGridThicknessPx = _shader.GetUniformLocation("uGridThicknessPx");
		_uCellColors = _shader.GetUniformLocation("uCellColors");

		_vao = new VertexArrayObject();
		_vao.Bind();

		// A unit quad in local space (0 to 1), the shader scales by cell size.
		_quadVbo = new BufferObject(BufferTarget.ArrayBuffer);
		_quadVbo.SetData<Vector2>(
		[
			new(0, 0),
			new(1, 0),
			new(1, 1),
			new(0, 1),
		], BufferUsageHint.StaticDraw);

		GL.EnableVertexAttribArray(0);
		GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, 0);

		// Instance positions, one per cell.
		_instanceVbo = new BufferObject(BufferTarget.ArrayBuffer);

		// IMPORTANT: bind the instance VBO before setting the vertex attrib pointer
		// so the VAO records the correct buffer binding for attribute 1.
		_instanceVbo.Bind();

		GL.EnableVertexAttribArray(1);
		GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, 0);
		GL.VertexAttribDivisor(1, 1);

		// Axis shader and buffers (lines from origin along +X and +Y)
		_axisShader = new ShaderProgram(Shaders.AxisVertex, Shaders.AxisFragment);

		_axisVao = new VertexArrayObject();
		_axisVao.Bind();

		_axisVbo = new BufferObject(BufferTarget.ArrayBuffer);
		// allocate initial empty buffer, will be filled when world is set
		_axisVbo.SetData<Vector2>([new(0, 0), new(0, 0), new(0, 0), new(0, 0)], BufferUsageHint.DynamicDraw);

		GL.EnableVertexAttribArray(0);
		GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, 0);

		Logger.Info("Renderer initialized.");
	}

	public void SetWorld(WorldModel world) {
		// Unsubscribe from previous world palette events if needed
		if (_world != null) {
			_world.SpeciesPaletteChanged -= OnWorldSpeciesPaletteChanged;
		}

		_world = world;

		// Subscribe to receive palette updates and copy initial palette
		_world.SpeciesPaletteChanged += OnWorldSpeciesPaletteChanged;
		_speciesPalette = _world.GetSpeciesPalette();
		BuildInstancePositions();
		BuildAxisBuffer();

		// Create and upload per-cell color texture for the new world.
		EnsureCellColorsTexture(_world.WidthCells, _world.HeightCells);
		UploadGridToTexture(_world.ActiveLayer.Grid);
	}

	public void Resize(int width, int height) {
		GL.Viewport(0, 0, width, height);
	}

	private void OnWorldSpeciesPaletteChanged(byte[] palette) {
		// Copy palette for renderer use. Keep a copy to avoid sharing memory with world.
		_speciesPalette = palette is null || palette.Length == 0 ? Array.Empty<byte>() : (byte[])palette.Clone();
	}

	public void Render(Camera camera) {
		GL.Clear(ClearBufferMask.ColorBufferBit);

		if (_world == null)
			return;

		_shader.Use();
		_vao.Bind();

		var viewProj = camera.GetViewProjection();
		GL.UniformMatrix4(_uViewProj, false, ref viewProj);

		GL.Uniform1(_uCellSize, _cellSize);
		GL.Uniform2(_uGridSize, new Vector2(_world.WidthCells, _world.HeightCells));

		// Grid control uniforms
		GL.Uniform1(_uShowGrid, ShowGrid ? 1 : 0);
		GL.Uniform1(_uPixelsPerUnit, camera.Zoom);
		GL.Uniform1(_uGridThicknessPx, GridThicknessPixels);

		// Bind per-cell color texture to unit 0
		if (_cellColorsTex != 0) {
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture2D, _cellColorsTex);
			GL.Uniform1(_uCellColors, 0);
		}

		// Draw as triangle fan per quad, instanced.
		// Later, you can draw only visible tiles for big worlds.
		GL.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, _instancePositions.Length);

		// Draw axes: X in red, Y in green
		if (_axisVbo != null && ShowAxes) {
			_axisShader.Use();
			_axisVao.Bind();
			int uViewProjAxis = _axisShader.GetUniformLocation("uViewProj");
			int uColor = _axisShader.GetUniformLocation("uColor");
			GL.UniformMatrix4(uViewProjAxis, false, ref viewProj);

			// X axis (first line)
			GL.Uniform3(uColor, new OpenTK.Mathematics.Vector3(1.0f, 0.1f, 0.1f));
			GL.DrawArrays(PrimitiveType.Lines, 0, 2);

			// Y axis (second line)
			GL.Uniform3(uColor, new OpenTK.Mathematics.Vector3(0.1f, 1.0f, 0.1f));
			GL.DrawArrays(PrimitiveType.Lines, 2, 2);
		}
	}

	// Ensure the color texture exists and matches dimensions.
	private void EnsureCellColorsTexture(int width, int height) {
		if (_cellColorsTex != 0) {
			// Check current size? For simplicity, recreate every time world changes.
			GL.DeleteTexture(_cellColorsTex);
			_cellColorsTex = 0;
		}

		_cellColorsTex = GL.GenTexture();
		GL.BindTexture(TextureTarget.Texture2D, _cellColorsTex);
		GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int) TextureMinFilter.Nearest);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int) TextureMagFilter.Nearest);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int) TextureWrapMode.ClampToEdge);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int) TextureWrapMode.ClampToEdge);
	}

	// Full upload of a CellGrid into the texture. Uses a temporary RGBA8 buffer and TexSubImage.
	private void UploadGridToTexture(CellGrid grid) {
		int w = grid.Width;
		int h = grid.Height;
		// Reuse a buffer for the full-grid upload to avoid per-frame allocations when possible.
		byte[] pixels = new byte[w * h * 4];

		// Map each cell byte through cached species palette (clamp index to last entry)
		int speciesCount = _speciesPalette.Length / 4;
		var src = grid.CurrentSpan;
		for (int i = 0, dst = 0; i < src.Length; i++, dst += 4) {
			byte value = src[i];
			if (speciesCount == 0) {
				pixels[dst + 0] = _defaultFallbackColor[0];
				pixels[dst + 1] = _defaultFallbackColor[1];
				pixels[dst + 2] = _defaultFallbackColor[2];
				pixels[dst + 3] = _defaultFallbackColor[3];
			} else {
				int useIdx = value < speciesCount ? value : (speciesCount - 1);
				int off = useIdx * 4;
				pixels[dst + 0] = _speciesPalette[off + 0];
				pixels[dst + 1] = _speciesPalette[off + 1];
				pixels[dst + 2] = _speciesPalette[off + 2];
				pixels[dst + 3] = _speciesPalette[off + 3];
			}
		}

		GCHandle gcHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
		try {
			GL.BindTexture(TextureTarget.Texture2D, _cellColorsTex);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, gcHandle.AddrOfPinnedObject());
		} finally {
			gcHandle.Free();
		}
	}

	// Update a single cell texel using TexSubImage2D for a 1x1 region.
	public void UploadSingleCell(CellGrid grid, int x, int y) {
		if (_cellColorsTex == 0)
			return;
		int w = grid.Width;
		int h = grid.Height;
		if (x < 0 || x >= w || y < 0 || y >= h)
			return;

		byte value = grid.CurrentSpan[grid.IndexOf(x, y)];
		// For a single texel, use a stack-allocated span to avoid heap alloc.
		Span<byte> pixelSpan = stackalloc byte[4];
		int speciesCount = _speciesPalette.Length / 4;
		if (speciesCount == 0) {
			pixelSpan[0] = _defaultFallbackColor[0];
			pixelSpan[1] = _defaultFallbackColor[1];
			pixelSpan[2] = _defaultFallbackColor[2];
			pixelSpan[3] = _defaultFallbackColor[3];
		} else {
			int useIdx = value < speciesCount ? value : (speciesCount - 1);
			int off = useIdx * 4;
			pixelSpan[0] = _speciesPalette[off + 0];
			pixelSpan[1] = _speciesPalette[off + 1];
			pixelSpan[2] = _speciesPalette[off + 2];
			pixelSpan[3] = _speciesPalette[off + 3];
		}

		// Use a temporary array when stack memory cannot be pinned; copy from stack to array.
		byte[] tmp = new byte[4];
		pixelSpan.CopyTo(tmp);
		GCHandle gcHandle = GCHandle.Alloc(tmp, GCHandleType.Pinned);
		try {
			GL.BindTexture(TextureTarget.Texture2D, _cellColorsTex);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, gcHandle.AddrOfPinnedObject());
		} finally {
			gcHandle.Free();
		}
	}

	private void BuildInstancePositions() {
		// This is a simple flat list of cell origins.
		// Later, you will replace this with chunked rendering for huge worlds,
		// and possibly a GPU generated instance buffer.
		int w = _world.WidthCells;
		int h = _world.HeightCells;

		_instancePositions = new Vector2[w * h];

		int idx = 0;
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++) {
				_instancePositions[idx++] = new Vector2(x * _cellSize, y * _cellSize);
			}

		_instanceVbo.SetData<Vector2>(_instancePositions, BufferUsageHint.StaticDraw);
	}

	private void BuildAxisBuffer() {
		if (_world == null)
			return;

		// Origin at (0,0). Endpoints along positive axes in world space.
		float maxX = _world.WidthCells * _cellSize;
		float maxY = _world.HeightCells * _cellSize;

		Vector2[] pts =
		[
			new Vector2(0, 0), // origin
			new Vector2(maxX, 0), // +X
			new Vector2(0, 0), // origin
			new Vector2(0, maxY), // +Y
		];

		_axisVbo.SetData<Vector2>(pts, BufferUsageHint.StaticDraw);
	}

	public void Dispose() {
		_instanceVbo?.Dispose();
		_quadVbo?.Dispose();
		_axisVbo?.Dispose();
		_axisVao?.Dispose();
		_vao?.Dispose();
		_shader?.Dispose();
		_axisShader?.Dispose();

		// Unsubscribe from world events to avoid leaking references
		if (_world != null) {
			_world.SpeciesPaletteChanged -= OnWorldSpeciesPaletteChanged;
		}

		if (_cellColorsTex != 0) {
			GL.DeleteTexture(_cellColorsTex);
			_cellColorsTex = 0;
		}
	}

	// Optional helper: update a rectangular region of cells (x,y,w,h)
	public void UploadCellsRegion(CellGrid grid, int x, int y, int w, int h) {
		if (_cellColorsTex == 0)
			return;
		int gw = grid.Width;
		int gh = grid.Height;
		int rx = Math.Max(0, x);
		int ry = Math.Max(0, y);
		int rw = Math.Min(w, gw - rx);
		int rh = Math.Min(h, gh - ry);
		if (rw <= 0 || rh <= 0)
			return;

		byte[] pixels = new byte[rw * rh * 4];
		int speciesCount = _speciesPalette.Length / 4;
		var src = grid.CurrentSpan;
		for (int yy = 0; yy < rh; yy++) {
			int sy = ry + yy;
			int rowBaseSrc = sy * grid.Width;
			int rowBaseDst = yy * rw * 4;
			for (int xx = 0; xx < rw; xx++) {
				int sx = rx + xx;
				byte value = src[rowBaseSrc + sx];
				int dst = rowBaseDst + xx * 4;
				if (speciesCount == 0) {
					pixels[dst + 0] = _defaultFallbackColor[0];
					pixels[dst + 1] = _defaultFallbackColor[1];
					pixels[dst + 2] = _defaultFallbackColor[2];
					pixels[dst + 3] = _defaultFallbackColor[3];
				} else {
					int useIdx = value < speciesCount ? value : (speciesCount - 1);
					int off = useIdx * 4;
					pixels[dst + 0] = _speciesPalette[off + 0];
					pixels[dst + 1] = _speciesPalette[off + 1];
					pixels[dst + 2] = _speciesPalette[off + 2];
					pixels[dst + 3] = _speciesPalette[off + 3];
				}
			}
		}

		GCHandle gcHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
		try {
			GL.BindTexture(TextureTarget.Texture2D, _cellColorsTex);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, rx, ry, rw, rh, PixelFormat.Rgba, PixelType.UnsignedByte, gcHandle.AddrOfPinnedObject());
		} finally {
			gcHandle.Free();
		}
	}
}
