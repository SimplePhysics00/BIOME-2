using Biome2.FileLoading;
using System;

namespace Biome2.World.CellGrid;

/// <summary>
/// Minimal abstraction for a cell grid so different topologies can implement their own coordinate systems.
/// Keep it intentionally small to reduce the amount of code that needs to switch immediately.
/// Implementations should provide GetCurrent/SetNext/Swap/Copy and dimension properties.
/// Also expose lightweight accessors used by renderer/updaters: CurrentSpan, NextSpan and IndexOf.
/// </summary>
public interface ICellGrid
{
    int Width { get; }
    int Height { get; }

    byte GetCurrent(int x, int y);
    void SetCurrent(int x, int y, byte value);
    void SetNext(int x, int y, byte value);

    void SwapBuffers();
    void CopyCurrentToNext();

    void Clear(byte value = 0);

    // Helpful low-level access for renderer texture uploads
    ReadOnlySpan<byte> CurrentSpan { get; }
    Span<byte> NextSpan { get; }
    int IndexOf(int x, int y);

    /// <summary>
    /// Populate the provided span with neighbor cell values for the logical neighbors
    /// of the cell at (x,y). Returns the number of neighbors written.
    /// The ordering for rectangular grids is the 8-neighborhood in row-major order
    /// skipping the center: (-1,-1),(0,-1),(1,-1),(-1,0),(1,0),(-1,1),(0,1),(1,1).
    /// Implementations should honor the supplied EdgeMode semantics when deciding
    /// whether to use an actual neighbor value or a backup sentinel value.
    /// </summary>
    int GetNeighbors(int x, int y, EdgeMode edgeMode, Span<byte> dest);
}
