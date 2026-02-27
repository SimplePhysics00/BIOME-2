using Biome2.Diagnostics;
using Biome2.Graphics.GlObjects;
using Biome2.Graphics.GLObjects;
using Biome2.World;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using static Biome2.Input.PlacementModes;
using Biome2.World.CellGrid;

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

    private ShaderProgram _shader = null!; // currently active shader
    private ShaderProgram _shaderRect = null!;
    private ShaderProgram _shaderHex = null!;
    private ShaderProgram _shaderDisk = null!;

    private ShaderProgram _axisShader = null!;

    private ShaderProgram? _highlightShader = null; // currently active highlight shader
    private ShaderProgram? _highlightShaderRect = null!;
    private ShaderProgram? _highlightShaderHex = null!;
    private ShaderProgram? _highlightShaderDisk = null!;

	private VertexArrayObject _vao = null!;
    private BufferObject _quadVbo = null!;
    private BufferObject _instanceVbo = null!;
    private VertexArrayObject? _highlightVao = null;
    private BufferObject? _highlightQuadVbo = null;
    private BufferObject? _highlightInstanceVbo = null;
    private VertexArrayObject _axisVao = null!;
    private BufferObject _axisVbo = null!;
    private BufferObject _instanceCellCoordVbo = null!;

    private WorldState _world = null!;
    public WorldState World => _world;
    // Debugging: ensure we only log hex-mode selection once to avoid spam
    private bool _didLogHexMode = false;

    private int _uViewProj;
    private int _uCellSize;
    private int _uGridSize;
    private int _uShowGrid;
    private int _uPixelsPerUnit;
    private int _uGridThicknessPx;
    private int _uCellIndices;
    private int _uPalette;
    private int _uSpeciesCount;
    private int _uUseTrapezoid;
    private int _uDiskCenter;
    private int _uUseHex;

    // Highlight shader uniforms
    private int _uHCellSize;
    private int _uHDiskCenter;
    private int _uHUseRect;
    private int _uHBorderThicknessPx;
    private int _uHDotFreq;
    private int _uHColorA;
    private int _uHColorB;
    private int _uHAlpha;

    // Controls for rendering options
    public bool ShowGrid { get; set; } = false;
    public bool ShowAxes { get; set; } = false;


    // When false, renderer will skip drawing; useful for pausing visual updates while
    // leaving simulation state intact. Default = true (drawing enabled).
    public bool DrawingEnabled { get; set; } = true;

    public float GridThicknessPixels { get; set; } = 1.0f;

    private Vector2[] _instancePositions = [];

    // Reusable upload buffers to avoid per-frame allocations
    private byte[]? _indexUploadBuffer;
    private byte[]? _paletteUploadBuffer;
    private byte[] _singleByte = new byte[1];
    private float[] _highlightInstanceData = new float[4];
    private byte[]? _regionUploadBuffer;

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
        // Reuse preallocated array to avoid allocations
        float[] instanceData = _highlightInstanceData;

        if (input.GetPlacementMode() == PlacementMode.PIXEL || !input.IsPlacing()) {
            if (hoverX >= 0 && hoverX < _world.WidthCells && hoverY >= 0 && hoverY < _world.HeightCells) {
                instanceCount = 1;
                // If disk topology, request world-centered instance data from the disk grid so highlight matches rendering
                if (_world.ActiveLayer?.Grid is DiskCellGrid disk && _world.GridTopology == GridTopology.SPIRAL) {
                    var inst = disk.GetCellWorldPosition(hoverX, hoverY, _cellSize);
                    instanceData[0] = inst.X;
                    instanceData[1] = inst.Y;
                    instanceData[2] = inst.Z;
                    instanceData[3] = inst.W;
                } else {
                    // For hex topology, compute the top-left of the hex bounding box so the
                    // highlight quad aligns with renderer instance placement.
                    if (_world.GridTopology == GridTopology.HEX && _world.ActiveLayer?.Grid is HexCellGrid) {
                        float hexH = _cellSize * 0.86602540378f;
                        float xStep = _cellSize * 0.75f;
                        float cx = hoverX * xStep;
                        float cy = hoverY * hexH + (((hoverX & 1) != 0) ? hexH * 0.5f : 0f);
                        instanceData[0] = cx - (_cellSize * 0.5f);
                        instanceData[1] = cy - (hexH * 0.5f);
                    } else {
                        instanceData[0] = hoverX * _cellSize;
                        instanceData[1] = hoverY * _cellSize;
                    }
                    instanceData[2] = 1.0f;
                    instanceData[3] = 1.0f;
                }
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
                if (_world.GridTopology == GridTopology.HEX && _world.ActiveLayer?.Grid is HexCellGrid) {
                    // For hex topology compute a rectangular bounding box in world space
                    // that covers the selected cells. Use the same hex geometry as
                    // instance placement: centers separated by xStep horizontally and
                    // hexH vertically. The rectangular highlight vertex shader expects
                    // aInstance.zw to be sizes in "cell units" multiplied by uCellSize,
                    // so convert world extents into those scaled units.
                    float hexH = _cellSize * 0.86602540378f;
                    float xStep = _cellSize * 0.75f;
                    // top-left center of the first cell
                    float cx0 = minX * xStep;
                    float cy0 = minY * hexH + (((minX & 1) != 0) ? hexH * 0.5f : 0f);
                    float originX = cx0 - (_cellSize * 0.5f);
                    float originY = cy0 - (hexH * 0.5f);
                    // world extents: width = (w-1)*xStep + cellSize, height = h*hexH
                    float worldW = (w - 1) * xStep + _cellSize;
                    float worldH = h * hexH;
                    // convert to the rectangular vertex shader's size units (size * uCellSize = worldExtent)
                    float sizeX = worldW / _cellSize;
                    float sizeY = worldH / _cellSize;
                    instanceData[0] = originX;
                    instanceData[1] = originY;
                    instanceData[2] = sizeX;
                    instanceData[3] = sizeY;
                } else {
                    instanceData[0] = minX * _cellSize;
                    instanceData[1] = minY * _cellSize;
                    instanceData[2] = (float)w;
                    instanceData[3] = (float)h;
                }
            }
        }

        if (instanceCount == 0) return;

        // Choose highlight shader per-frame. For hex topology + zone placement we
        // need the rectangular highlight vertex shader so region sizing is correct.
        ShaderProgram shaderToUse = _highlightShader!;
        if (input.GetPlacementMode() == PlacementMode.ZONE && _world.GridTopology == GridTopology.HEX) {
            shaderToUse = _highlightShaderRect!;
        }
        // Use the chosen highlight program and VAO first, then upload instance data
        // so the VAO records the correct buffer binding on all drivers.
        shaderToUse.Use();
        _highlightVao!.Bind();
        // Upload instance data (vec4 per instance: origin.x, origin.y, size.x, size.y)
        _highlightInstanceVbo.Bind();
        _highlightInstanceVbo.SetData<float>(instanceData, BufferUsageHint.DynamicDraw);

        var viewProj = camera.GetViewProjection();
        // Query uniform locations from the chosen shader and upload values. This
        // avoids depending on cached locations which may belong to a different
        // highlight shader variant.
        int uViewProjH = shaderToUse.GetUniformLocation("uViewProj");
        int uCellSizeH = shaderToUse.GetUniformLocation("uCellSize");
        int uUseTrapezoidH = shaderToUse.GetUniformLocation("uUseTrapezoid");
        int uUseHexH = shaderToUse.GetUniformLocation("uUseHex");
        int uDiskCenterH = shaderToUse.GetUniformLocation("uDiskCenter");
        int uTimeH = shaderToUse.GetUniformLocation("uTime");
        int uPixelsPerUnitH = shaderToUse.GetUniformLocation("uPixelsPerUnit");
        int uUseRectH = shaderToUse.GetUniformLocation("uUseRect");
        int uBorderThicknessH = shaderToUse.GetUniformLocation("uBorderThicknessPx");
        int uDotFreqH = shaderToUse.GetUniformLocation("uDotFrequency");
        int uColorAH = shaderToUse.GetUniformLocation("uColorA");
        int uColorBH = shaderToUse.GetUniformLocation("uColorB");
        int uAlphaH = shaderToUse.GetUniformLocation("uAlpha");

        if (uViewProjH >= 0) GL.UniformMatrix4(uViewProjH, false, ref viewProj);
        if (uCellSizeH >= 0) GL.Uniform1(uCellSizeH, _cellSize);
        if (uUseTrapezoidH >= 0) GL.Uniform1(uUseTrapezoidH, _world.GridTopology == GridTopology.SPIRAL ? 1 : 0);
        if (uUseHexH >= 0) GL.Uniform1(uUseHexH, _world.GridTopology == GridTopology.HEX ? 1 : 0);
        // pass disk center for highlight calculations
        if (uDiskCenterH >= 0) {
            if (_world.ActiveLayer?.Grid is DiskCellGrid diskGrid) {
                var dc = diskGrid.GetBackingGridCenter(_cellSize);
                GL.Uniform2(uDiskCenterH, ref dc);
            } else {
                Vector2 diskCenter = new Vector2(((_world.WidthCells - 1) * _cellSize) * 0.5f, ((_world.HeightCells - 1) * _cellSize) * 0.5f);
                GL.Uniform2(uDiskCenterH, ref diskCenter);
            }
        }
        if (uTimeH >= 0) GL.Uniform1(uTimeH, (float)DateTime.Now.TimeOfDay.TotalSeconds);
        if (uPixelsPerUnitH >= 0) GL.Uniform1(uPixelsPerUnitH, camera.Zoom);
        // If using hex topology and user is doing a zone placement, request rectangular highlight
        int useRect = (input.GetPlacementMode() == PlacementMode.ZONE && _world.GridTopology == GridTopology.HEX) ? 1 : 0;
        if (uUseRectH >= 0) GL.Uniform1(uUseRectH, useRect);
        if (uBorderThicknessH >= 0) GL.Uniform1(uBorderThicknessH, 2.0f);
        if (uDotFreqH >= 0) GL.Uniform1(uDotFreqH, 4.0f);
        if (uColorAH >= 0) GL.Uniform3(uColorAH, new OpenTK.Mathematics.Vector3(0f,0f,0f));
        if (uColorBH >= 0) GL.Uniform3(uColorBH, new OpenTK.Mathematics.Vector3(1f,1f,1f));
        if (uAlphaH >= 0) GL.Uniform1(uAlphaH, 1.0f);

        // Ensure blending is enabled for highlight overlays (some topology draws disable blending)
        bool wasBlendEnabled = GL.IsEnabled(EnableCap.Blend);
        if (!wasBlendEnabled) GL.Enable(EnableCap.Blend);

        GL.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, instanceCount);

        if (!wasBlendEnabled) GL.Disable(EnableCap.Blend);
    }

    public void Initialize() {
		GL.ClearColor(0.08f, 0.08f, 0.10f, 1.0f);
		GL.Enable(EnableCap.Blend);
		GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Build shader variants for each topology. By default compile
        // a rectangular shader (fast path) and a hex-specific fragment
        // variant that enforces a precise hex interior. The disk/trapezoid
        // topology uses the rectangular fragment but will enable trapezoid
        // behaviour via a uniform on the shared vertex shader.

        // Build shader programs per-topology using the split shader sources.
        _shaderRect = new ShaderProgram(Shaders.GridVertexRect, Shaders.GridFragmentRect);
        _shaderHex = new ShaderProgram(Shaders.GridVertexHex, Shaders.GridFragmentHex);
        _shaderDisk = new ShaderProgram(Shaders.GridVertexDisk, Shaders.GridFragmentRect);

		// Default active shader is rectangular
		SetActiveGridShader(_shaderRect);

		// Now create VAO/VBOs and other GL objects once. These do not depend on
		// the specific shader variant but must be created after a program exists
		// so attribute locations are stable on older GL implementations.

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

		// Instance VBO will contain per-instance attributes. We'll use two attributes:
		// attrib 1 (vec4): origin.x, origin.y, angle, pad
		// attrib 2 (vec2): cellX, cellY (logical coords)
		_instanceVbo = new BufferObject(BufferTarget.ArrayBuffer);

		// IMPORTANT: bind the instance VBO before setting the vertex attrib pointer
		// so the VAO records the correct buffer binding for attribute 1.
		_instanceVbo.Bind();

		GL.EnableVertexAttribArray(1);
		GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, sizeof(float) * 4, 0);
		GL.VertexAttribDivisor(1, 1);

		// We'll bind a second attribute from a separate VBO for cell coords.
		// Create a small VBO for the logical coords (vec2 per instance).
		_instanceCellCoordVbo = new BufferObject(BufferTarget.ArrayBuffer);
		_instanceCellCoordVbo.Bind();
		GL.EnableVertexAttribArray(2);
		GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, 0);
		GL.VertexAttribDivisor(2, 1);

        // Axis shader and buffers (lines from origin along +X and +Y)
        _axisShader = new ShaderProgram(Shaders.AxisVertex, Shaders.AxisFragment);

        // Build highlight shader variants
        _highlightShaderRect = new ShaderProgram(Shaders.HighlightVertexRect, Shaders.HighlightFragmentRect);
        _highlightShaderHex = new ShaderProgram(Shaders.HighlightVertexHex, Shaders.HighlightFragmentHex);
        _highlightShaderDisk = new ShaderProgram(Shaders.HighlightVertexDisk, Shaders.HighlightFragmentRect);
        
        // store one of them in the nullable holder for legacy references
        SetActiveHighlightShader(_highlightShaderRect);

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

		// Select appropriate shader variant for this world's topology.
		switch (_world.GridTopology) {
			case GridTopology.HEX:
				SetActiveGridShader(_shaderHex);
				SetActiveHighlightShader(_highlightShaderHex);
				break;
			case GridTopology.SPIRAL:
				SetActiveGridShader(_shaderDisk);
				SetActiveHighlightShader(_highlightShaderDisk);
				break;
			default:
				SetActiveGridShader(_shaderRect);
				SetActiveHighlightShader(_highlightShaderRect);
				break;
		}
	}

	private void SetActiveGridShader(ShaderProgram shader) {
        _shader = shader;
		// Query uniform locations on the active shader program so later Uniform calls
		// write to the correct program without needing to rebind every time.
		_uViewProj = _shader.GetUniformLocation("uViewProj");
		_uCellSize = _shader.GetUniformLocation("uCellSize");
		_uGridSize = _shader.GetUniformLocation("uGridSize");
		_uShowGrid = _shader.GetUniformLocation("uShowGrid");
		_uPixelsPerUnit = _shader.GetUniformLocation("uPixelsPerUnit");
		_uGridThicknessPx = _shader.GetUniformLocation("uGridThicknessPx");
		_uCellIndices = _shader.GetUniformLocation("uCellIndices");
		_uPalette = _shader.GetUniformLocation("uPalette");
		_uSpeciesCount = _shader.GetUniformLocation("uSpeciesCount");
		_uUseTrapezoid = _shader.GetUniformLocation("uUseTrapezoid");
		_uDiskCenter = _shader.GetUniformLocation("uDiskCenter");
		_uUseHex = _shader.GetUniformLocation("uUseHex");

	}

    private void SetActiveHighlightShader(ShaderProgram? shader) {
        _highlightShader = shader;
        if (shader == null) return;
        _uHCellSize = shader.GetUniformLocation("uCellSize");
        _uHUseRect = shader.GetUniformLocation("uUseRect");
        _uHDiskCenter = shader.GetUniformLocation("uDiskCenter");
        _uHBorderThicknessPx = shader.GetUniformLocation("uBorderThicknessPx");
        _uHDotFreq = shader.GetUniformLocation("uDotFrequency");
        _uHColorA = shader.GetUniformLocation("uColorA");
        _uHColorB = shader.GetUniformLocation("uColorB");
        _uHAlpha = shader.GetUniformLocation("uAlpha");
        // Bind the program and initialize sensible defaults so the highlight is visible
        shader.Use();
        if (_uHCellSize >= 0) GL.Uniform1(_uHCellSize, _cellSize);
        if (_uHUseRect >= 0) GL.Uniform1(_uHUseRect, 0);
        if (_uHAlpha >= 0) GL.Uniform1(_uHAlpha, 1.0f);
        if (_uHBorderThicknessPx >= 0) GL.Uniform1(_uHBorderThicknessPx, 2.0f);
        if (_uHDotFreq >= 0) GL.Uniform1(_uHDotFreq, 4.0f);
        if (_uHColorA >= 0) GL.Uniform3(_uHColorA, new Vector3(0f, 0f, 0f));
        if (_uHColorB >= 0) GL.Uniform3(_uHColorB, new Vector3(1f, 1f, 1f));
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
		if (_world.GridTopology == GridTopology.HEX) {
			if (!_didLogHexMode) { Logger.Info("Renderer: HEX topology active - using hex fragment path."); _didLogHexMode = true; }
		} else {
			if (_didLogHexMode) { Logger.Info("Renderer: EXITTED HEX topology."); _didLogHexMode = false; }
		}

		var viewProj = camera.GetViewProjection();
		GL.UniformMatrix4(_uViewProj, false, ref viewProj);

		GL.Uniform1(_uCellSize, _cellSize);

		GL.Uniform1(_uUseTrapezoid, _world.GridTopology == GridTopology.SPIRAL ? 1 : 0);
		// disk center in world coordinates (backing-grid centered)
		if (_world.ActiveLayer?.Grid is DiskCellGrid disk) {
			var dc = disk.GetBackingGridCenter(_cellSize);
			GL.Uniform2(_uDiskCenter, ref dc);
			GL.Uniform2(_uHDiskCenter, ref dc);
		} else {
			Vector2 diskCenter = new Vector2(((_world.WidthCells - 1) * _cellSize) * 0.5f, ((_world.HeightCells - 1) * _cellSize) * 0.5f);
			GL.Uniform2(_uDiskCenter, ref diskCenter);
			GL.Uniform2(_uHDiskCenter, ref diskCenter);
		}
		
		GL.Uniform2(_uGridSize, new Vector2(_world.WidthCells, _world.HeightCells));

		// Grid control uniforms
		GL.Uniform1(_uShowGrid, ShowGrid ? 1 : 0);
		GL.Uniform1(_uPixelsPerUnit, camera.Zoom);
		GL.Uniform1(_uGridThicknessPx, GridThicknessPixels);
		GL.Uniform1(_uUseHex, _world.GridTopology == GridTopology.HEX ? 1 : 0);

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
        // For hex topology we disable blending so discarded fragments do not blend
        // with underlying quads (prevents rectangular alpha artifacts).
        bool wasBlendEnabled = GL.IsEnabled(EnableCap.Blend);
        if (_world.GridTopology == GridTopology.HEX) {
            GL.Disable(EnableCap.Blend);
        }
        GL.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, _instancePositions.Length);
        if (_world.GridTopology == GridTopology.HEX && wasBlendEnabled) {
            GL.Enable(EnableCap.Blend);
        }

		// Draw axes: X in red, Y in green
		if (_axisVbo != null && ShowAxes) {
			_axisShader.Use();
			_axisVao.Bind();
			int uViewProjAxis = _axisShader.GetUniformLocation("uViewProj");
			int uColor = _axisShader.GetUniformLocation("uColor");
			GL.UniformMatrix4(uViewProjAxis, false, ref viewProj);

			// X axis (first line)
			GL.Uniform3(uColor, new Vector3(1.0f, 0.1f, 0.1f));
			GL.DrawArrays(PrimitiveType.Lines, 0, 2);

			// Y axis (second line)
			GL.Uniform3(uColor, new Vector3(0.1f, 1.0f, 0.1f));
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
    private void UploadGridToTexture(ICellGrid grid) {
        int w = grid.Width;
        int h = grid.Height;
        int len = w * h;
        if (_indexUploadBuffer == null || _indexUploadBuffer.Length < len) {
            _indexUploadBuffer = new byte[len];
        }
        var src = grid.CurrentSpan;
        for (int i = 0; i < src.Length; i++) {
            _indexUploadBuffer[i] = src[i];
        }

        GCHandle gcHandle = GCHandle.Alloc(_indexUploadBuffer, GCHandleType.Pinned);
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
        int palLen = speciesCount * 4;
        if (_paletteUploadBuffer == null || _paletteUploadBuffer.Length < palLen) _paletteUploadBuffer = new byte[palLen];
        if (_speciesPalette.Length == 0) {
            _paletteUploadBuffer[0] = _defaultFallbackColor[0];
            _paletteUploadBuffer[1] = _defaultFallbackColor[1];
            _paletteUploadBuffer[2] = _defaultFallbackColor[2];
            _paletteUploadBuffer[3] = _defaultFallbackColor[3];
        } else {
            System.Buffer.BlockCopy(_speciesPalette, 0, _paletteUploadBuffer, 0, palLen);
        }

        GCHandle palHandle = GCHandle.Alloc(_paletteUploadBuffer, GCHandleType.Pinned);
        try {
            GL.BindTexture(TextureTarget.Texture2D, _paletteTex);
            // palette is RGBA8 so default unpack alignment (4) is ok
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, speciesCount, 1, PixelFormat.Rgba, PixelType.UnsignedByte, palHandle.AddrOfPinnedObject());
        } finally {
            palHandle.Free();
        }
	}

	// Update a single cell texel using TexSubImage2D for a 1x1 region.
    public void UploadSingleCell(ICellGrid grid, int x, int y) {
		if (_cellIndexTex == 0)
			return;
        int w = grid.Width;
        int h = grid.Height;
		if (x < 0 || x >= w || y < 0 || y >= h)
			return;

        byte value = grid.CurrentSpan[grid.IndexOf(x, y)];
        _singleByte[0] = value;
        GCHandle gcHandle = GCHandle.Alloc(_singleByte, GCHandleType.Pinned);
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
		// Support topology-aware instance positions. For rectangular grids we emit
		// one instance per x,y cell. For Disk topology we request instance data
		// from the grid which provides world-space positions per valid cell.
        // We'll build two arrays: one for vec4 instance attribute (origin.x, origin.y, angle, pad)
        // and one for vec2 logical cell coords (cellX, cellY).
        var grid = _world.ActiveLayer.Grid;
        var instances = new List<float>();
        var cellCoords = new List<float>();
        if (grid is DiskCellGrid disk) {
            var inst = disk.GetInstanceData(_cellSize);
            _instancePositions = new Vector2[inst.Length];
            for (int i = 0; i < inst.Length; i++) {
                // instance positions returned by disk are now centered around origin
                _instancePositions[i] = new Vector2(inst[i].X, inst[i].Y);
                instances.Add(inst[i].X);
                instances.Add(inst[i].Y);
                instances.Add(inst[i].Z); // angle
                instances.Add(inst[i].W); // pad
                // logical coords: ring = ? we must recompute counts array mapping
                // DiskCellGrid does not currently expose per-instance logical coords; compute them here by enumerating rings again
                // We'll use a helper mapping: for each ring r, positions p=0..cnt-1 emitted in same order as GetInstanceData
                // So we can reconstruct ring and p by iterating in same nested loops.
                // To avoid redoing logic, the DiskCellGrid should ideally return both, but for now we will compute below.
            }
            // Build logical coords by iterating rings again
            for (int r = 0; r < disk.RingsCount; r++) {
                int cnt = disk.RingCountAt(r);
                for (int p = 0; p < cnt; p++) {
                    cellCoords.Add(r);
                    cellCoords.Add(p);
                }
            }
        } else if (grid is HexCellGrid hex) {
            int w = _world.WidthCells;
            int h = _world.HeightCells;
            var posList = new List<Vector2>();
            float hexH = _cellSize * 0.86602540378f; // sqrt(3)/2
            float xStep = _cellSize * 0.75f; // horizontal spacing between centers
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    if (!hex.IsValidCell(x, y)) continue;
                    // compute center for flat-top hex
                    float cx = x * xStep;
                    float cy = y * hexH + (((x & 1) != 0) ? hexH * 0.5f : 0f);
                    // origin = top-left of bounding box
                    float ox = cx - (_cellSize * 0.5f);
                    float oy = cy - (hexH * 0.5f);
                    posList.Add(new Vector2(ox, oy));
                    instances.Add(ox);
                    instances.Add(oy);
                    instances.Add(0f);
                    instances.Add(0f);
                    cellCoords.Add(x);
                    cellCoords.Add(y);
                }
            }
            _instancePositions = posList.ToArray();
        } else {
            int w = _world.WidthCells;
            int h = _world.HeightCells;
            _instancePositions = new Vector2[w * h];
            int idx = 0;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    var px = x * _cellSize;
                    var py = y * _cellSize;
                    _instancePositions[idx++] = new Vector2(px, py);
                    instances.Add(px);
                    instances.Add(py);
                    instances.Add(0f); // angle unused
                    instances.Add(0f);
                    cellCoords.Add(x);
                    cellCoords.Add(y);
                }
            }
        }

        // Upload as floats
        _instanceVbo.SetData<float>(instances.ToArray(), BufferUsageHint.StaticDraw);
        _instanceCellCoordVbo.Bind();
        _instanceCellCoordVbo.SetData<float>(cellCoords.ToArray(), BufferUsageHint.StaticDraw);
	}

	private void BuildAxisBuffer() {
		if (_world == null)
			return;

		// Origin at (0,0). Endpoints along positive axes in world space.
		float maxX = 0f;
		float maxY = 0f;
        Vector2[] pts;
        if (_instancePositions != null && _instancePositions.Length > 0) {
            // If disk topology, compute extents relative to disk center
            if (_world.GridTopology == GridTopology.SPIRAL && _world.ActiveLayer?.Grid is DiskCellGrid disk) {
                Vector2 center = disk.GetBackingGridCenter(_cellSize);
                float maxRelX = 0f;
                float maxRelY = 0f;
                for (int i = 0; i < _instancePositions.Length; i++) {
                    var p = _instancePositions[i];
                    float rx = Math.Abs(p.X - center.X);
                    float ry = Math.Abs(p.Y - center.Y);
                    if (rx > maxRelX) maxRelX = rx;
                    if (ry > maxRelY) maxRelY = ry;
                }
                maxRelX += _cellSize;
                maxRelY += _cellSize;
                pts = new Vector2[] {
                    new Vector2(center.X, center.Y),
                    new Vector2(center.X + maxRelX, center.Y),
                    new Vector2(center.X, center.Y),
                    new Vector2(center.X, center.Y + maxRelY),
                };
            } else {
                for (int i = 0; i < _instancePositions.Length; i++) {
                    var p = _instancePositions[i];
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
                // extend to cover one cell
                maxX += _cellSize;
                maxY += _cellSize;
                pts = new Vector2[] {
                    new Vector2(0, 0), // origin
                    new Vector2(maxX, 0), // +X
                    new Vector2(0, 0), // origin
                    new Vector2(0, maxY), // +Y
                };
            }
        } else {
            maxX = _world.WidthCells * _cellSize;
            maxY = _world.HeightCells * _cellSize;
            pts = new Vector2[] {
                new Vector2(0, 0), // origin
                new Vector2(maxX, 0), // +X
                new Vector2(0, 0), // origin
                new Vector2(0, maxY), // +Y
            };
        }

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
    public void UploadCellsRegion(ICellGrid grid, int x, int y, int w, int h) {
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

        int len = rw * rh;
        if (_regionUploadBuffer == null || _regionUploadBuffer.Length < len) _regionUploadBuffer = new byte[len];
        byte[] indices = _regionUploadBuffer;
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
