using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Biome2.Graphics.UI;

namespace Biome2.Graphics;

// Minimal ImGui controller adapted for ImGui.NET + OpenTK/OpenGL.
// Provides a simple UI window that can toggle Renderer.ShowGrid and SimulationClock.Paused.
public sealed class ImGuiController : IDisposable
{
    private GameWindow _window;
    private int _vertexArray;
    private int _fontTexture;
    private GLObjects.ShaderProgram? _shader;
    private int _vbo;
    private int _ibo;
    private readonly ToolboxWindow _toolbox = new ToolboxWindow();

    // For now we rely on ImGui's internal state only.
    public ImGuiController(GameWindow window)
    {
        _window = window;

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;

        // Setup style
        ImGui.StyleColorsDark();
		// Build fonts and upload texture to the GPU so ImGui rendering won't assert.
		io.Fonts.AddFontDefault();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);
		ImGui.GetStyle().ScaleAllSizes(2.5f);
		_fontTexture = GL.GenTexture();

        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        // Tell ImGui about the texture id so it doesn't try to rebuild fonts at render time.
        io.Fonts.SetTexID((IntPtr)_fontTexture);
        // We can clear CPU-side font data after uploading
        io.Fonts.ClearTexData();

        // Setup backends
        // Fonts and other init can go here.

        // Create VAO placeholder
        _vertexArray = GL.GenVertexArray();

        // Create shader and buffers for minimal ImGui GL rendering.
        const string vertexSrc = "#version 330 core\n" +
            "layout (location = 0) in vec2 in_pos;\n" +
            "layout (location = 1) in vec2 in_uv;\n" +
            "layout (location = 2) in vec4 in_col;\n" +
            "out vec2 frag_uv;\n" +
            "out vec4 frag_col;\n" +
            "uniform mat4 proj_mat;\n" +
            "void main() { frag_uv = in_uv; frag_col = in_col; gl_Position = proj_mat * vec4(in_pos, 0, 1); }";

        const string fragmentSrc = "#version 330 core\n" +
            "in vec2 frag_uv;\n" +
            "in vec4 frag_col;\n" +
            "uniform sampler2D Texture;\n" +
            "out vec4 out_col;\n" +
            "void main() { out_col = texture(Texture, frag_uv) * frag_col; }";

        try
        {
            _shader = new Graphics.GLObjects.ShaderProgram(vertexSrc, fragmentSrc);
        }
        catch
        {
            _shader = null;
        }

        _vbo = GL.GenBuffer();
        _ibo = GL.GenBuffer();

        // Setup VAO attribute layout (position, uv, color)
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        // ImGui's ImDrawVert native layout is (float pos[2], float uv[2], uint col) => 2*4 + 2*4 + 4 = 20 bytes
        // Use the explicit native stride to avoid managed layout/padding mismatches.
        int stride = 20;
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

        GL.BindVertexArray(0);

        // Hook input events
        _window.MouseDown += OnMouseDown;
        _window.MouseUp += OnMouseUp;
        _window.MouseMove += OnMouseMove;
        _window.MouseWheel += OnMouseWheel;
        _window.KeyDown += OnKeyDown;
        _window.KeyUp += OnKeyUp;
        _window.TextInput += OnTextInput;

        SetPerFrameData();
    }

    public void RenderUI(Renderer renderer, Simulation.SimulationController simulation, Input.InputState input, Camera camera)
    {
        // Start new frame
        SetPerFrameData();
        ImGui.NewFrame();

        // Render the toolbox window
        _toolbox.Render(renderer, simulation, input);

        // Tiny bottom-right hover overlay: show species name under mouse on active layer.
        try
        {
            var world = simulation.World;
            if (world != null)
            {
                // Determine hovered cell using input helper
                var hover = input.GetHoverCell(camera, renderer);
                int hx = hover.X;
                int hy = hover.Y;

                if (hx >= 0 && hy >= 0 && hx < world.WidthCells && hy < world.HeightCells)
                {
                    var grid = world.ActiveLayer.Grid;
                    byte value = grid.GetCurrent(hx, hy);
                    string name = world.GetSpeciesName(value);
                    if (!string.IsNullOrEmpty(name))
                    {
                        var io = ImGui.GetIO();
                        // Position at bottom-right using pivot (1,1)
                        //ImGui.SetNextWindowBgAlpha(0.06f);
                        ImGui.SetNextWindowPos(new System.Numerics.Vector2(io.DisplaySize.X - 10f, io.DisplaySize.Y - 10f), ImGuiCond.Always, new System.Numerics.Vector2(1f, 1f));
                        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(6f, 4f));
                        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings
                                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoInputs
                                    | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoBackground;
                        ImGui.SetNextWindowBgAlpha(0.0f);
                        ImGui.Begin("##hover_overlay", flags);
                        ImGui.SetWindowFontScale(1.5f);
                        ImGui.Text(name);
                        ImGui.End();
                        ImGui.PopStyleVar();
                    }
                }
            }
        }
        catch
        {
            // Swallow any UI errors to avoid interfering with main rendering loop
        }

        ImGui.Render();
        var drawData = ImGui.GetDrawData();

        RenderImDrawData(drawData);
    }

    private void SetPerFrameData()
    {
        var io = ImGui.GetIO();
        // Query the current GL viewport to determine the actual framebuffer size in pixels.
        // This avoids mismatches when window scaling / title-bar offsets differ from logical window size.
        var vp = new int[4];
        GL.GetInteger(GetPName.Viewport, vp);
        io.DisplaySize = new System.Numerics.Vector2(vp[2], vp[3]);
        // When framebuffer size == display size, scale is 1.0. If DPI scaling is used this
        // value should reflect framebuffer/display ratio; using viewport here keeps things correct.
        io.DisplayFramebufferScale = new System.Numerics.Vector2(1.0f, 1.0f);
        io.DeltaTime = 1.0f / 60.0f; // caller should update with real delta later if needed
    }

    private void RenderImDrawData(ImDrawDataPtr drawData)
    {
        // If no draw lists, nothing to do
        if (drawData.CmdListsCount == 0) return;

        var prevViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, prevViewport);

        GL.GetInteger(GetPName.CurrentProgram, out int lastProgram);
        GL.GetInteger(GetPName.TextureBinding2D, out int lastTexture);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int lastArrayBuffer);
        GL.GetInteger(GetPName.ElementArrayBufferBinding, out int lastElementArrayBuffer);

        bool wasBlendEnabled = GL.IsEnabled(EnableCap.Blend);
        bool wasCullFaceEnabled = GL.IsEnabled(EnableCap.CullFace);
        bool wasDepthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);
        bool wasScissorEnabled = GL.IsEnabled(EnableCap.ScissorTest);

        // Setup render state
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);

		var displayPos = drawData.DisplayPos;
		var displaySize = drawData.DisplaySize;

		// Use drawData's DisplayPos/DisplaySize so multiple viewports and framebuffer sizes are handled.
		GL.Viewport((int)displayPos.X, (int)displayPos.Y, (int)displaySize.X, (int)displaySize.Y);

        // Upload vertex/index data into a single VBO/IBO
        // Use the same explicit vertex size as above (native ImDrawVert = 20 bytes)
        int vtxSize = 20;
        int idxSize = Marshal.SizeOf(typeof(ushort));
        int totalVtxBytes = drawData.TotalVtxCount * vtxSize;
        int totalIdxBytes = drawData.TotalIdxCount * idxSize;

        if (totalVtxBytes == 0 || totalIdxBytes == 0)
        {
            // restore state
            if (!wasBlendEnabled) GL.Disable(EnableCap.Blend);
            if (wasCullFaceEnabled) GL.Enable(EnableCap.CullFace);
            if (wasDepthTestEnabled) GL.Enable(EnableCap.DepthTest);
            if (!wasScissorEnabled) GL.Disable(EnableCap.ScissorTest);
            GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
            return;
        }

        var vtxData = new byte[totalVtxBytes];
        var idxData = new byte[totalIdxBytes];

        int vtxOffset = 0;
        int idxOffset = 0;
        int vtxVertexBase = 0; // number of vertices already copied (for index adjustment)
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            int vtxBytes = cmdList.VtxBuffer.Size * vtxSize;
            int idxBytes = cmdList.IdxBuffer.Size * idxSize;
            if (vtxBytes > 0)
            {
                Marshal.Copy(cmdList.VtxBuffer.Data, vtxData, vtxOffset, vtxBytes);
                vtxOffset += vtxBytes;
            }
            if (idxBytes > 0)
            {
                // Copy raw index bytes then reinterpret as little-endian 16-bit values so we can add vertex base.
                int idxCount = cmdList.IdxBuffer.Size;
                var raw = new byte[idxBytes];
                Marshal.Copy(cmdList.IdxBuffer.Data, raw, 0, idxBytes);

                // Decode, adjust, and write back into the destination idxData buffer.
                for (int i = 0; i < idxCount; i++)
                {
                    int b0 = raw[i * 2];
                    int b1 = raw[i * 2 + 1];
                    ushort v = (ushort)(b0 | (b1 << 8));
                    v = (ushort)(v + vtxVertexBase);
                    idxData[idxOffset + i * 2] = (byte)(v & 0xFF);
                    idxData[idxOffset + i * 2 + 1] = (byte)((v >> 8) & 0xFF);
                }
                idxOffset += idxBytes;
            }
            vtxVertexBase += cmdList.VtxBuffer.Size;
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, totalVtxBytes, vtxData, BufferUsageHint.StreamDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, totalIdxBytes, idxData, BufferUsageHint.StreamDraw);

        // Setup orthographic projection using drawData DisplayPos/DisplaySize to handle viewports
        var io = ImGui.GetIO();
        var proj = Matrix4.CreateOrthographicOffCenter(
            displayPos.X,
            displayPos.X + displaySize.X,
            displayPos.Y + displaySize.Y,
            displayPos.Y,
            1.0f,
            -1.0f);

        // Use shader
        if (_shader == null)
        {
            // nothing to draw without shader
            goto restore;
        }

        _shader.Use();
        int loc = _shader.GetUniformLocation("proj_mat");
        GL.UniformMatrix4(loc, false, ref proj);
        int texLoc = _shader.GetUniformLocation("Texture");
        GL.Uniform1(texLoc, 0);

        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ibo);

        // Draw
        int globalIdxOffset = 0;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            for (int cmd_i = 0; cmd_i < cmdList.CmdBuffer.Size; cmd_i++)
            {
                var pcmd = cmdList.CmdBuffer[cmd_i];
                if (pcmd.UserCallback != IntPtr.Zero)
                {
                    // User callback not supported in this minimal renderer
                }
                else
                {
                    int texId = (int)pcmd.TextureId;
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, texId);

                    // Convert clip rect to framebuffer coordinates (account for DisplayPos and framebuffer scale)
                    var clip = pcmd.ClipRect;
                    float clipX = (clip.X - displayPos.X) * io.DisplayFramebufferScale.X;
                    float clipY = (clip.Y - displayPos.Y) * io.DisplayFramebufferScale.Y;
                    float clipZ = (clip.Z - displayPos.X) * io.DisplayFramebufferScale.X;
                    float clipW = (clip.W - displayPos.Y) * io.DisplayFramebufferScale.Y;

                    int scissorX = (int)Math.Floor(clipX);
                    int scissorY = (int)Math.Floor((displaySize.Y * io.DisplayFramebufferScale.Y) - clipW);
                    int scissorW = (int)Math.Ceiling(clipZ - clipX);
                    int scissorH = (int)Math.Ceiling(clipW - clipY);
                    if (scissorW > 0 && scissorH > 0)
                        GL.Scissor(scissorX, scissorY, scissorW, scissorH);

                    GL.DrawElements(PrimitiveType.Triangles, (int)pcmd.ElemCount, DrawElementsType.UnsignedShort, (int)(globalIdxOffset * idxSize));
                }

                globalIdxOffset += (int)pcmd.ElemCount;
            }
        }

    restore:
        // Restore modified GL state
        GL.BindTexture(TextureTarget.Texture2D, lastTexture);
        GL.UseProgram(lastProgram);
        GL.BindBuffer(BufferTarget.ArrayBuffer, lastArrayBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, lastElementArrayBuffer);
        GL.BindVertexArray(0);

        if (!wasBlendEnabled) GL.Disable(EnableCap.Blend);
        if (wasCullFaceEnabled) GL.Enable(EnableCap.CullFace);
        if (wasDepthTestEnabled) GL.Enable(EnableCap.DepthTest);
        if (!wasScissorEnabled) GL.Disable(EnableCap.ScissorTest);

        GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
    }

    public void UpdateInputState(Input.InputState state)
    {
        var io = ImGui.GetIO();

        // Use event-based input API on ImGuiIOPtr to set current state
        // Scale mouse coordinates by the framebuffer scale so ImGui receives coordinates in the
        // same coordinate space as DisplaySize/Viewport.
        io.AddMousePosEvent(state.MouseX * io.DisplayFramebufferScale.X, state.MouseY * io.DisplayFramebufferScale.Y);
        io.AddMouseButtonEvent(0, state.MouseLeftDown);
        io.AddMouseButtonEvent(1, state.MouseRightDown);
        io.AddMouseButtonEvent(2, state.MouseMiddleDown);

        // Reflect whether ImGui wants to capture the mouse/keyboard so app can block world interactions.
        state.SetGuiWants(io.WantCaptureMouse, io.WantCaptureKeyboard);
    }

    // Simple event handlers to feed ImGui IO
    private void OnMouseDown(MouseButtonEventArgs e)
    {
        var io = ImGui.GetIO();
        io.AddMouseButtonEvent((int)e.Button, true);
    }

    private void OnMouseUp(MouseButtonEventArgs e)
    {
        var io = ImGui.GetIO();
        io.AddMouseButtonEvent((int)e.Button, false);
    }

    private void OnMouseMove(MouseMoveEventArgs e)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(e.Position.X * io.DisplayFramebufferScale.X, e.Position.Y * io.DisplayFramebufferScale.Y);
    }

    private void OnMouseWheel(MouseWheelEventArgs e)
    {
        var io = ImGui.GetIO();
        // AddMouseWheelEvent takes (x, y) wheel deltas; preserve previous behavior (vertical wheel).
        io.AddMouseWheelEvent(0.0f, (float)e.OffsetY);
    }

    private void OnKeyDown(KeyboardKeyEventArgs e)
    {
        var io = ImGui.GetIO();
        var key = ToImGuiKey(e.Key);
        if (key != ImGuiKey.None)
        {
            io.AddKeyEvent(key, true);
        }
    }

    private void OnKeyUp(KeyboardKeyEventArgs e)
    {
        var io = ImGui.GetIO();
        var key = ToImGuiKey(e.Key);
        if (key != ImGuiKey.None)
        {
            io.AddKeyEvent(key, false);
        }
    }

    private void OnTextInput(TextInputEventArgs e)
    {
        var io = ImGui.GetIO();
        // In some OpenTK versions TextInputEventArgs provides a Unicode codepoint (int).
        // Use AddInputCharactersUTF8 with a UTF-8 string constructed from the codepoint.
        try
        {
            string s = char.ConvertFromUtf32(e.Unicode);
            io.AddInputCharactersUTF8(s);
        }
        catch
        {
            // Fallback: ignore invalid codepoints.
        }
    }

    // Map OpenTK Keys to ImGuiNET ImGuiKey.
    // Keep mapping conservative to avoid referencing enum members that may not exist across OpenTK versions.
    private static ImGuiKey ToImGuiKey(OpenTK.Windowing.GraphicsLibraryFramework.Keys key)
    {
        // Letters A..Z (assumes contiguous ranges)
        if (key >= OpenTK.Windowing.GraphicsLibraryFramework.Keys.A && key <= OpenTK.Windowing.GraphicsLibraryFramework.Keys.Z)
        {
            return (ImGuiKey)((int)ImGuiKey.A + ((int)key - (int)OpenTK.Windowing.GraphicsLibraryFramework.Keys.A));
        }

        // Function keys F1..F12 (assumes contiguous ranges)
        if (key >= OpenTK.Windowing.GraphicsLibraryFramework.Keys.F1 && key <= OpenTK.Windowing.GraphicsLibraryFramework.Keys.F12)
        {
            return (ImGuiKey)((int)ImGuiKey.F1 + ((int)key - (int)OpenTK.Windowing.GraphicsLibraryFramework.Keys.F1));
        }

        switch (key)
        {
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Tab: return ImGuiKey.Tab;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Left: return ImGuiKey.LeftArrow;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Right: return ImGuiKey.RightArrow;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Up: return ImGuiKey.UpArrow;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Down: return ImGuiKey.DownArrow;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.PageUp: return ImGuiKey.PageUp;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.PageDown: return ImGuiKey.PageDown;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Home: return ImGuiKey.Home;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.End: return ImGuiKey.End;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Insert: return ImGuiKey.Insert;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Delete: return ImGuiKey.Delete;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Backspace: return ImGuiKey.Backspace;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space: return ImGuiKey.Space;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Enter: return ImGuiKey.Enter;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape: return ImGuiKey.Escape;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.LeftShift: return ImGuiKey.LeftShift;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.RightShift: return ImGuiKey.RightShift;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.LeftControl: return ImGuiKey.LeftCtrl;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.RightControl: return ImGuiKey.RightCtrl;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.LeftAlt: return ImGuiKey.LeftAlt;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.RightAlt: return ImGuiKey.RightAlt;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.CapsLock: return ImGuiKey.CapsLock;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.ScrollLock: return ImGuiKey.ScrollLock;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.NumLock: return ImGuiKey.NumLock;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.PrintScreen: return ImGuiKey.PrintScreen;
            case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Pause: return ImGuiKey.Pause;
            default:
                return ImGuiKey.None;
        }
    }

    public void Dispose()
    {
        _window.MouseDown -= OnMouseDown;
        _window.MouseUp -= OnMouseUp;
        _window.MouseMove -= OnMouseMove;
        _window.MouseWheel -= OnMouseWheel;
        _window.KeyDown -= OnKeyDown;
        _window.KeyUp -= OnKeyUp;
        _window.TextInput -= OnTextInput;

        ImGui.DestroyContext();
        if (_fontTexture != 0)
        {
            GL.DeleteTexture(_fontTexture);
            _fontTexture = 0;
        }
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_ibo != 0) GL.DeleteBuffer(_ibo);
        GL.DeleteVertexArray(_vertexArray);
        if (_shader != null) _shader.Dispose();
    }
}
