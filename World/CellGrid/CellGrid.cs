namespace Biome2.World.CellGrid;

/// <summary>
/// One layer of cells. Uses double buffering for safe parallel simulation.
/// </summary>
public sealed class CellGrid {
	public int Width { get; }
	public int Height { get; }

	private byte[] _current;
	private byte[] _next;

	public ReadOnlySpan<byte> CurrentSpan => _current;
	public Span<byte> NextSpan => _next;

	public CellGrid(int width, int height) {
		Width = width;
		Height = height;

		_current = new byte[width * height];
		_next = new byte[width * height];
	}

	public int IndexOf(int x, int y) => y * Width + x;

	public byte GetCurrent(int x, int y) => _current[IndexOf(x, y)];
    public void SetCurrent(int x, int y, byte value) => _current[IndexOf(x, y)] = value;
	public void SetNext(int x, int y, byte value) => _next[IndexOf(x, y)] = value;

	public void Clear(byte value = 0) {
		Array.Fill(_current, value);
		Array.Fill(_next, value);
	}

	public void FillWith(byte[] allowedValues) {
		// fills the buffers with a random array of values from the allowed list
		ArgumentNullException.ThrowIfNull(allowedValues);
		if (allowedValues.Length == 0)
			throw new ArgumentException("allowedValues must contain at least one value", nameof(allowedValues));

		var rand = Random.Shared;
		int len = Width * Height;
		for (int i = 0; i < len; i++) {
			byte v = allowedValues[rand.Next(allowedValues.Length)];
			_current[i] = v;
			_next[i] = v;
		}
	}

	public void SwapBuffers() {
		(_current, _next) = (_next, _current);
	}

	public void CopyCurrentToNext() {
		Buffer.BlockCopy(_current, 0, _next, 0, _current.Length);
	}
}
