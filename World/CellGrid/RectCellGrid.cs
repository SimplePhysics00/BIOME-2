
namespace Biome2.World.CellGrid;

/// <summary>
/// Adapter that exposes existing CellGrid via the ICellGrid interface.
/// This is a transitional helper; later hex/spiral grids will provide their own implementations.
/// </summary>
public sealed class RectCellGrid : ICellGrid
{
    private readonly DataGrid _dataGrid;
    // Public properties
    public int Width => _dataGrid.Width;
    public int Height => _dataGrid.Height;

    public ReadOnlySpan<byte> CurrentSpan => _dataGrid.CurrentSpan;
    public Span<byte> NextSpan => _dataGrid.NextSpan;

    public int IndexOf(int x, int y) => _dataGrid.IndexOf(x, y);

    public bool IsValidCell(int x, int y) => x >= 0 && x < _dataGrid.Width && y >= 0 && y < _dataGrid.Height;

    public byte GetCurrent(int x, int y) => _dataGrid.GetCurrent(x, y);

    public void SetCurrent(int x, int y, byte value) => _dataGrid.SetCurrent(x, y, value);

    public void SetNext(int x, int y, byte value) => _dataGrid.SetNext(x, y, value);

    public void SwapBuffers() => _dataGrid.SwapBuffers();

    public void CopyCurrentToNext() => _dataGrid.CopyCurrentToNext();

    public void Clear(byte value = 0) => _dataGrid.Clear(value); 

    // Constructors
    public RectCellGrid(int width, int height) {
        _dataGrid = new DataGrid(width, height);
    }

    public int GetNeighbors(int x, int y, EdgeMode edgeMode, Span<byte> dest) {
        //if (dest.Length < 8) throw new ArgumentException("GetNeighbors parameter 'dest' must be at least length 8", nameof(dest));

        int ni = 0;
        int width = _dataGrid.Width;
        int height = _dataGrid.Height;

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

                dest[ni++] = useNeighbor ? _dataGrid.GetCurrent(nx, ny) : backupNeighborValue;
            }
        }

        return ni;
    }

    public int GetNeighborCoordinates(int x, int y, EdgeMode edgeMode, Span<int> destX, Span<int> destY) {
        //if (destX.Length < 8 || destY.Length < 8) throw new ArgumentException("destX/destY must be at least length 8");
        
        int ni = 0;
        int width = _dataGrid.Width;
        int height = _dataGrid.Height;

        for (int oy = -1; oy <= 1; oy++) {
            for (int ox = -1; ox <= 1; ox++) {
                if (ox == 0 && oy == 0) continue;

                int nx = x + ox;
                int ny = y + oy;

                bool outOfX = nx < 0 || nx >= width;
                bool outOfY = ny < 0 || ny >= height;

                bool useNeighbor = true;

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
                        break;
                    default:
                        if (outOfX || outOfY) useNeighbor = false;
                        break;
                }

                if (useNeighbor) {
                    destX[ni] = nx;
                    destY[ni] = ny;
                } else {
                    destX[ni] = -1;
                    destY[ni] = -1;
                }
                ni++;
            }
        }

        return ni;
    }
}
