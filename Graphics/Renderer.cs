using Biome2.Diagnostics;
using Biome2.Graphics.GlObjects;
using Biome2.Graphics.GLObjects;
using Biome2.World;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Biome2.Graphics;

// TODO: look into using CUDA for enhanced benefits & CPU / GPU balancing

/// <summary>
/// Renders the active world layer as a grid of cells.
/// Uses instancing, one quad per cell, and colors by cell state.
/// Today everything is empty, later you will map values to palettes.
/// </summary>
public sealed class Renderer(float cellSize) : IDisposable {
	private readonly float _cellSize = cellSize;
	public float CellSize => _cellSize;

	private ShaderProgram _shader = null!;
	private ShaderProgram _axisShader = null!;
    private ShaderProgram? _highlightShader = null;

	private VertexArrayObject _vao = null!;
	private BufferObject _quadVbo = null!;
	private BufferObject _instanceVbo = null!;
	private VertexArrayObject? _highlightVao = null;
	private BufferObject? _highlightQuadVbo = null;
	private BufferObject? _highlightInstanceVbo = null;
	private VertexArrayObject _axisVao = null!;
	private BufferObject _axisVbo = null!;

    private WorldState _world = null!;

	private int _uViewProj;
	private int _uCellSize;
	private int _uGridSize;
	private int _uShowGrid;
	private int _uPixelsPerUnit;
	private int _uGridThicknessPx;
	private int _uCellIndices;
	private int _uPalette;
	private int _uSpeciesCount;

	// Highlight shader uniforms
	private int _uHViewProj;
	private int _uHCellSize;
	private int _uHTime;
	private int _uHPixelsPerUnit;
	private int _uHBorderThicknessPx;
	private int _uHDotFreq;
	private int _uHColorA;
	private int _uHColorB;
	private int _uHAlpha;

	// Controls for rendering options
	public bool ShowGrid { get; set; } = false;
	public bool ShowAxes { get; set; } = true;

    // When false, renderer will skip drawing; useful for pausing visual updates while
    // leaving simulation state intact. Default = true (drawing enabled).
    public bool DrawingEnabled { get; set; } = true;

	public float GridThicknessPixels { get; set; } = 1.0f;

	private Vector2[] _instancePositions = [];

	// Per-cell color texture (RGBA8). Each cell maps to one texel.
	// Per-cell species index texture (R8). Each cell stores a single byte index.
	private int _cellIndexTex = 0;
	// Palette texture storing RGBA8 colors in a 1xN texture.
	private int _paletteTex = 0;

	// Cached flattened RGBA8 palette copied from WorldModel on species changes.
	// Length = speciesCount * 4. Always at least one RGBA entry (fallback) to simplify shader lookups.
	private byte[] _speciesPalette = [];

    // default fallback color when palette empty
    private static readonly byte[] _defaultFallbackColor = [255, 255, 255, 255];

	// Draw highlight overlay based on input state (hover or zone). Call this from app after Render.
	public void DrawHighlight(Camera camera, Input.InputState input) {
		if (_highlightShader == null || _highlightVao == null || _highlightInstanceVbo == null)
			return;

		if (_world == null) return;

		// Determine hover cell
		var (X, Y)= input.GetHoverCell(camera, this);
		int hoverX = X;
		int hoverY = Y;

		int instanceCount = 0;
		float[] instanceData = [];

		if (input.GetPlacementMode() == Input.InputState.PlacementMode.Pixel || !input.IsPlacing()) {
			if (hoverX >= 0 && hoverX < _world.WidthCells && hoverY >= 0 && hoverY < _world.HeightCells) {
				instanceCount = 1;
				instanceData = [hoverX * _cellSize, hoverY * _cellSize, 1.0f, 1.0f];
			}
		} else {
			var start = input.GetPlacementStart();
			int sx = start.X;
			int sy = start.Y;
			if (sx >= 0 && sy >= 0) {
				int minX = Math.Min(sx, hoverX);
				int maxX = Math.Max(sx, hoverX);
				int minY = Math.Min(sy, hoverY);
				int maxY = Math.Max(sy, hoverY);
				int w = maxX - minX + 1;
				int h = maxY - minY + 1;
				instanceCount = 1;
				instanceData = [minX * _cellSize, minY * _cellSize, (float)w, (float)h];
			}
		}

		if (instanceCount == 0) return;

		// Upload instance data (vec4 per instance: origin.x, origin.y, size.x, size.y)
		_highlightInstanceVbo.Bind();
		_highlightInstanceVbo.SetData<float>(instanceData, BufferUsageHint.DynamicDraw);

		_highlightShader!.Use();
		_highlightVao!.Bind();

		var viewProj = camera.GetViewProjection();
		GL.UniformMatrix4(_uHViewProj, false, ref viewProj);
		GL.Uniform1(_uHCellSize, _cellSize);
		GL.Uniform1(_uHTime, (float)DateTime.Now.TimeOfDay.TotalSeconds);
		GL.Uniform1(_uHPixelsPerUnit, camera.Zoom);
		GL.Uniform1(_uHBorderThicknessPx, 2.0f);
		GL.Uniform1(_uHDotFreq, 4.0f);
		GL.Uniform3(_uHColorA, new OpenTK.Mathematics.Vector3(0f,0f,0f));
		GL.Uniform3(_uHColorB, new OpenTK.Mathematics.Vector3(1f,1f,1f));
		GL.Uniform1(_uHAlpha, 1.0f);

		GL.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, instanceCount);
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
		_uCellIndices = _shader.GetUniformLocation("uCellIndices");
		_uPalette = _shader.GetUniformLocation("uPalette");
		_uSpeciesCount = _shader.GetUniformLocation("uSpeciesCount");

		_vao = new VertexArrayObject();
		_vao.Bind();

		// A unit quad in local space (0 to 1), the shader scales by cell size.
        _quadVbo = new BufferObject(BufferTarget.ArrayBuffer);
        _quadVbo.SetData<Vector2>([
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1),
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

		// Highlight shader
		_highlightShader = new ShaderProgram(Shaders.HighlightVertex, Shaders.HighlightFragment);

		_uHViewProj = _highlightShader.GetUniformLocation("uViewProj");
		_uHCellSize = _highlightShader.GetUniformLocation("uCellSize");
		_uHTime = _highlightShader.GetUniformLocation("uTime");
		_uHPixelsPerUnit = _highlightShader.GetUniformLocation("uPixelsPerUnit");
		_uHBorderThicknessPx = _highlightShader.GetUniformLocation("uBorderThicknessPx");
		_uHDotFreq = _highlightShader.GetUniformLocation("uDotFrequency");
		_uHColorA = _highlightShader.GetUniformLocation("uColorA");
		_uHColorB = _highlightShader.GetUniformLocation("uColorB");
		_uHAlpha = _highlightShader.GetUniformLocation("uAlpha");

		_axisVao = new VertexArrayObject();
		_axisVao.Bind();

        _axisVbo = new BufferObject(BufferTarget.ArrayBuffer);
        // allocate initial empty buffer, will be filled when world is set
        _axisVbo.SetData<Vector2>([new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0)], BufferUsageHint.DynamicDraw);

		GL.EnableVertexAttribArray(0);
		GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, 0);

		// Setup highlight VAO (unit quad 0..1)
		_highlightVao = new VertexArrayObject();
		_highlightVao.Bind();
		_highlightQuadVbo = new BufferObject(BufferTarget.ArrayBuffer);
		_highlightQuadVbo.SetData<Vector2>([new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1)], BufferUsageHint.StaticDraw);
		GL.EnableVertexAttribArray(0);
		GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, 0);

		// Instance buffer: vec2 origin, ivec2 size -> we can send as vec4 (x,y,sizeX,sizeY)
		_highlightInstanceVbo = new BufferObject(BufferTarget.ArrayBuffer);
		_highlightInstanceVbo.Bind();
		GL.EnableVertexAttribArray(1);
		GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, sizeof(float) * 4, 0);
		GL.VertexAttribDivisor(1, 1);

		Logger.Info("Renderer initialized.");
	}

    public void SetWorld(WorldState world) {
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
		EnsureIndexAndPaletteTextures(_world.WidthCells, _world.HeightCells);
		UploadGridToTexture(_world.ActiveLayer.Grid);
	}

	public static void Resize(int width, int height) {
		GL.Viewport(0, 0, width, height);
	}

	private void OnWorldSpeciesPaletteChanged(byte[] palette) {
		// The world now publishes a cloned palette payload. Keep a local copy
		// so the renderer does not share owned arrays with the world.
		_speciesPalette = (palette is null || palette.Length == 0) ? [] : (byte[])palette.Clone();
		UploadGridToTexture(_world.ActiveLayer.Grid);
	}

	public void Render(Camera camera) {
		// If drawing is disabled, skip world rendering entirely so the last
		// drawn frame remains visible and only the UI will be updated.
		if (!DrawingEnabled) return;

		GL.Clear(ClearBufferMask.ColorBufferBit);

		if (_world == null)
			return;

		// Ensure GPU texture is up-to-date with the current active layer before drawing.
		// This keeps rendering decoupled from simulation stepping and allows the
		// renderer to update at its own cadence.
		UploadGridToTexture(_world.ActiveLayer.Grid);

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

		// Bind per-cell index texture to unit 0 and palette to unit 1
		if (_cellIndexTex != 0) {
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture2D, _cellIndexTex);
			GL.Uniform1(_uCellIndices, 0);
		}
		if (_paletteTex != 0) {
			GL.ActiveTexture(TextureUnit.Texture1);
			GL.BindTexture(TextureTarget.Texture2D, _paletteTex);
			GL.Uniform1(_uPalette, 1);
		}
		GL.Uniform1(_uSpeciesCount, _speciesPalette.Length / 4);

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

	// Ensure the index texture and palette texture exist and match dimensions.
	private void EnsureIndexAndPaletteTextures(int width, int height) {
		if (_cellIndexTex != 0) {
			GL.DeleteTexture(_cellIndexTex);
			_cellIndexTex = 0;
		}
		_cellIndexTex = GL.GenTexture();
		GL.BindTexture(TextureTarget.Texture2D, _cellIndexTex);
		// R8 internal format, single byte per texel
		GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, width, height, 0, PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

		// Palette texture: 1xN RGBA8
		if (_paletteTex != 0) {
			GL.DeleteTexture(_paletteTex);
			_paletteTex = 0;
		}
		_paletteTex = GL.GenTexture();
		GL.BindTexture(TextureTarget.Texture2D, _paletteTex);
		// If palette empty we still allocate 1 texel so lookups are valid.
		int speciesCount = Math.Max(1, _speciesPalette.Length / 4);
		GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, speciesCount, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
		GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
	}

	// Full upload of a CellGrid into the index texture (R8). Also ensure the palette texture
	// is uploaded from the cached _speciesPalette.
	private void UploadGridToTexture(CellGrid grid) {
		int w = grid.Width;
		int h = grid.Height;
		byte[] indices = new byte[w * h];
		var src = grid.CurrentSpan;
		for (int i = 0; i < src.Length; i++) {
			indices[i] = src[i];
		}

		GCHandle gcHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
		try {
			GL.BindTexture(TextureTarget.Texture2D, _cellIndexTex);
			// Ensure single-byte row alignment for R8 uploads (default UNPACK_ALIGNMENT=4 would stride incorrectly)
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, PixelFormat.Red, PixelType.UnsignedByte, gcHandle.AddrOfPinnedObject());
			// restore default alignment
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
		} finally {
			gcHandle.Free();
		}

		// Upload palette texture (1xN) from _speciesPalette. If empty, upload single fallback color.
		int speciesCount = Math.Max(1, _speciesPalette.Length / 4);
		byte[] pal = new byte[speciesCount * 4];
		if (_speciesPalette.Length == 0) {
			pal[0] = _defaultFallbackColor[0];
			pal[1] = _defaultFallbackColor[1];
			pal[2] = _defaultFallbackColor[2];
			pal[3] = _defaultFallbackColor[3];
		} else {
			System.Buffer.BlockCopy(_speciesPalette, 0, pal, 0, pal.Length);
		}

		GCHandle palHandle = GCHandle.Alloc(pal, GCHandleType.Pinned);
		try {
			GL.BindTexture(TextureTarget.Texture2D, _paletteTex);
			// palette is RGBA8 so default unpack alignment (4) is ok
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, speciesCount, 1, PixelFormat.Rgba, PixelType.UnsignedByte, palHandle.AddrOfPinnedObject());
		} finally {
			palHandle.Free();
		}
	}

	// Update a single cell texel using TexSubImage2D for a 1x1 region.
	public void UploadSingleCell(CellGrid grid, int x, int y) {
		if (_cellIndexTex == 0)
			return;
		int w = grid.Width;
		int h = grid.Height;
		if (x < 0 || x >= w || y < 0 || y >= h)
			return;

		byte value = grid.CurrentSpan[grid.IndexOf(x, y)];
		byte[] tmp = new byte[1] { value };
		GCHandle gcHandle = GCHandle.Alloc(tmp, GCHandleType.Pinned);
		try {
			GL.BindTexture(TextureTarget.Texture2D, _cellIndexTex);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, 1, 1, PixelFormat.Red, PixelType.UnsignedByte, gcHandle.AddrOfPinnedObject());
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
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

        Vector2[] pts = [
            new(0, 0), // origin
            new(maxX, 0), // +X
            new(0, 0), // origin
            new(0, maxY), // +Y
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

		if (_cellIndexTex != 0) {
			GL.DeleteTexture(_cellIndexTex);
			_cellIndexTex = 0;
		}
		if (_paletteTex != 0) {
			GL.DeleteTexture(_paletteTex);
			_paletteTex = 0;
		}
	}

	// Optional helper: update a rectangular region of cells (x,y,w,h)
	public void UploadCellsRegion(CellGrid grid, int x, int y, int w, int h) {
		if (_cellIndexTex == 0)
			return;
		int gw = grid.Width;
		int gh = grid.Height;
		int rx = Math.Max(0, x);
		int ry = Math.Max(0, y);
		int rw = Math.Min(w, gw - rx);
		int rh = Math.Min(h, gh - ry);
		if (rw <= 0 || rh <= 0)
			return;

		byte[] indices = new byte[rw * rh];
		var src = grid.CurrentSpan;
		for (int yy = 0; yy < rh; yy++) {
			int sy = ry + yy;
			int rowBaseSrc = sy * grid.Width;
			int rowBaseDst = yy * rw;
			for (int xx = 0; xx < rw; xx++) {
				int sx = rx + xx;
				byte value = src[rowBaseSrc + sx];
				indices[rowBaseDst + xx] = value;
			}
		}

		GCHandle gcHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
		try {
			GL.BindTexture(TextureTarget.Texture2D, _cellIndexTex);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, rx, ry, rw, rh, PixelFormat.Red, PixelType.UnsignedByte, gcHandle.AddrOfPinnedObject());
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
		} finally {
			gcHandle.Free();
		}
	}
}
