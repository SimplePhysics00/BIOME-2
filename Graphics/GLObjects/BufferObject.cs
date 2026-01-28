using OpenTK.Graphics.OpenGL4;

namespace Biome2.Graphics.GlObjects;

public sealed class BufferObject : IDisposable {
	public int Handle { get; }
	private readonly BufferTarget _target;

	public BufferObject(BufferTarget target) {
		_target = target;
		Handle = GL.GenBuffer();
	}

	public void Bind() => GL.BindBuffer(_target, Handle);

    public void SetData<T>(ReadOnlySpan<T> data, BufferUsageHint usage) where T : unmanaged {
        Bind();
        int sizeOfT = System.Runtime.InteropServices.Marshal.SizeOf<T>();
        long byteLength = (long)data.Length * sizeOfT;

        if (data.Length == 0) {
            GL.BufferData(_target, IntPtr.Zero, IntPtr.Zero, usage);
            return;
        }

        // Copy span into a temporary array and upload. Using ToArray ensures a contiguous buffer.
        T[] tmp = data.ToArray();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(tmp, System.Runtime.InteropServices.GCHandleType.Pinned);
        try {
            GL.BufferData(_target, new IntPtr(byteLength), handle.AddrOfPinnedObject(), usage);
        } finally {
            handle.Free();
        }
    }

	public void Dispose() {
		GL.DeleteBuffer(Handle);
	}
}
