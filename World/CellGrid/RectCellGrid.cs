using System;
using Biome2.FileLoading;

namespace Biome2.World.CellGrid;

/// <summary>
/// Adapter that exposes existing CellGrid via the ICellGrid interface.
/// This is a transitional helper; later hex/spiral grids will provide their own implementations.
/// </summary>
public sealed class RectCellGrid : ICellGrid
{
    private readonly CellGrid _inner;

    public RectCellGrid(int width, int height) {
        _inner = new CellGrid(width, height);
    }

    public RectCellGrid(CellGrid existing) {
        _inner = existing ?? throw new ArgumentNullException(nameof(existing));
    }

    public int Width => _inner.Width;
    public int Height => _inner.Height;

    public byte GetCurrent(int x, int y) => _inner.GetCurrent(x, y);
    public void SetCurrent(int x, int y, byte value) => _inner.SetCurrent(x, y, value);
    public void SetNext(int x, int y, byte value) => _inner.SetNext(x, y, value);
    public void SwapBuffers() => _inner.SwapBuffers();
    public void CopyCurrentToNext() => _inner.CopyCurrentToNext();
    public void Clear(byte value = 0) => _inner.Clear(value);

    public ReadOnlySpan<byte> CurrentSpan => _inner.CurrentSpan;
    public Span<byte> NextSpan => _inner.NextSpan;
    public int IndexOf(int x, int y) => _inner.IndexOf(x, y);

    public int GetNeighbors(int x, int y, EdgeMode edgeMode, Span<byte> dest) {
        if (dest.Length < 8) throw new ArgumentException("dest must be at least length 8", nameof(dest));

        int ni = 0;
        int width = _inner.Width;
        int height = _inner.Height;

        for (int oy = -1; oy <= 1; oy++) {
            for (int ox = -1; ox <= 1; ox++) {
                if (ox == 0 && oy == 0) continue;

                int nx = x + ox;
                int ny = y + oy;

                bool outOfX = nx < 0 || nx >= width;
                bool outOfY = ny < 0 || ny >= height;

                bool useNeighbor = true;
                byte backupNeighborValue = 255;

                switch (edgeMode) {
                    case EdgeMode.WRAP:
                        if (nx < 0) nx += width;
                        else if (nx >= width) nx -= width;
                        if (ny < 0) ny += height;
                        else if (ny >= height) ny -= height;
                        break;
                    case EdgeMode.WRAPX:
                        if (nx < 0) nx += width;
                        else if (nx >= width) nx -= width;
                        if (outOfY) useNeighbor = false;
                        break;
                    case EdgeMode.WRAPY:
                        if (ny < 0) ny += height;
                        else if (ny >= height) ny -= height;
                        if (outOfX) useNeighbor = false;
                        break;
                    case EdgeMode.INFINITE:
                        if (outOfX || outOfY) useNeighbor = false;
                        backupNeighborValue = 0;
                        break;
                    default:
                        if (outOfX || outOfY) useNeighbor = false;
                        break;
                }

                dest[ni++] = useNeighbor ? _inner.GetCurrent(nx, ny) : backupNeighborValue;
            }
        }

        return ni;
    }
}
