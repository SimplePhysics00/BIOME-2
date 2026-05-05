using OpenTK.Mathematics;

namespace Biome2.World.CellGrid;

// TODO: Some cells have more than 8 neighbors and some have less. An attempt was made to enforce 8, but 
//  the logic is complex and may have edge cases.
/// <summary>
/// Disk-shaped grid implemented on top of a rectangular backing store.
/// Logical coords: x = ring (0..rings-1), y = position along ring (0..ringCount-1)
/// This implementation uses the rectangular CellGrid as storage with width == rings
/// and height == outerCount. Only positions with y < ringCount[x] are considered valid.
/// </summary>
public sealed class DiskCellGrid : ICellGrid
{
    // Fields
    private readonly DataGrid _dataGrid;

    private readonly int _rings;
    private readonly int[] _ringCounts;
    private readonly int _outerCount;

    // Public properties
    public int RingsCount => _rings;
    public int Width => _dataGrid.Width;
    public int Height => _dataGrid.Height;

    public ReadOnlySpan<byte> CurrentSpan => _dataGrid.CurrentSpan;
    public Span<byte> NextSpan => _dataGrid.NextSpan;

    // Methods (constructors + public API)
    public DiskCellGrid(int rings, int outerCount)
    {
        _rings = Math.Max(1, rings);
        _outerCount = Math.Max(3, outerCount);

        // underlying storage: columns = rings, rows = outerCount
        _dataGrid = new DataGrid(_rings, _outerCount);
        _ringCounts = ComputeRingCounts(_rings, _outerCount);
    }

    /// <summary>
    /// Compute the linear instance index for a given logical (ring,pos) in the same
    /// ordering used by GetInstanceData (rings outer loop, positions inner loop).
    /// Returns -1 if invalid.
    /// </summary>
    public int GetInstanceIndex(int ring, int pos)
    {
        if (ring < 0 || ring >= _rings) return -1;
        int cnt = _ringCounts[ring];
        if (pos < 0 || pos >= cnt) return -1;
        int idx = 0;
        for (int r = 0; r < ring; r++) idx += _ringCounts[r];
        idx += pos;
        return idx;
    }

    /// <summary>
    /// Return the instance Vector4 for the given logical coords. This is the same
    /// value that appears in the array returned by GetInstanceData.
    /// </summary>
    public Vector4 GetInstanceAt(int ring, int pos, float cellSize)
    {
        if (!IsValidCell(ring, pos)) return new Vector4(0,0,0,0);
        return GetCellWorldPosition(ring, pos, cellSize);
    }

    // Expose helper properties used by renderer to build logical coords list
    public int RingCountAt(int ring) => (ring >= 0 && ring < _rings) ? _ringCounts[ring] : 0;

    public int IndexOf(int x, int y) => _dataGrid.IndexOf(x, y);

    public bool IsValidCell(int ring, int pos)
    {
        if (ring < 0 || ring >= _rings) return false;
        int cnt = _ringCounts[ring];
        return pos >= 0 && pos < cnt;
    }

    public byte GetCurrent(int x, int y)
    {
        if (!IsValidCell(x, y)) return 0;
        return _dataGrid.GetCurrent(x, y);
    }

    public void SetCurrent(int x, int y, byte value)
    {
        if (!IsValidCell(x, y)) return;
        _dataGrid.SetCurrent(x, y, value);
    }

    public void SetNext(int x, int y, byte value)
    {
        if (!IsValidCell(x, y)) return;
        _dataGrid.SetNext(x, y, value);
    }

    public void SwapBuffers() => _dataGrid.SwapBuffers();
    public void CopyCurrentToNext() => _dataGrid.CopyCurrentToNext();
    public void Clear(byte value = 0) => _dataGrid.Clear(value);

    /// <summary>
    /// Populate dest with the eight neighbor values for logical disk neighbors.
    /// Ordering: inner-left, inner-center, inner-right, same-left, same-right, outer-left, outer-center, outer-right
    /// Returns number of entries written (will be 8).
    /// </summary>
    public int GetNeighbors(int x, int y, EdgeMode edgeMode, Span<byte> dest)
    {
        //if (dest.Length < 8) throw new ArgumentException("dest must be at least length 8", nameof(dest));

        byte backupBorder = 255;
        byte backupInfinite = 0;
        byte backup = backupBorder;

        int ni = 0;
        int r = x;
        int p = y;

        if (r < 0 || r >= _rings)
        {
            for (int i = 0; i < 8; i++) dest[ni++] = backup;
            return ni;
        }

        int curCount = _ringCounts[r];

        byte Fetch(int rr, int pp, out bool used)
        {
            used = true;
            if (rr < 0 || rr >= _rings)
            {
                if (edgeMode == EdgeMode.WRAP || edgeMode == EdgeMode.WRAPX || edgeMode == EdgeMode.WRAPY)
                {
                    if (rr < 0) rr = (rr % _rings + _rings) % _rings;
                    else rr = rr % _rings;
                }
                else
                {
                    used = false;
                    return edgeMode == EdgeMode.INFINITE ? backupInfinite : backupBorder;
                }
            }

            int cnt = _ringCounts[rr];
            if (cnt == 0)
            {
                used = false;
                return edgeMode == EdgeMode.INFINITE ? backupInfinite : backupBorder;
            }

            int wrapped = ((pp % cnt) + cnt) % cnt;
            if (!IsValidCell(rr, wrapped))
            {
                used = false;
                return edgeMode == EdgeMode.INFINITE ? backupInfinite : backupBorder;
            }

            return _dataGrid.GetCurrent(rr, wrapped);
        }

        // Inner ring neighbors
        if (r == 0)
        {
            if (edgeMode == EdgeMode.WRAP)
            {
                int innerR = _rings - 1;
                int innerCnt = _ringCounts[innerR];
                if (innerCnt > 0)
                {
                    double frac = (double)p / Math.Max(1, curCount);
                    int center = (int)Math.Round(frac * innerCnt) % innerCnt;
                    dest[ni++] = Fetch(innerR, center - 1, out _);
                    dest[ni++] = Fetch(innerR, center, out _);
                    dest[ni++] = Fetch(innerR, center + 1, out _);
                }
                else { dest[ni++] = backup; dest[ni++] = backup; dest[ni++] = backup; }
            }
            else { dest[ni++] = backup; dest[ni++] = backup; dest[ni++] = backup; }
        }
        else
        {
            int innerR = r - 1;
            int innerCnt = _ringCounts[innerR];
            double frac = (double)p / Math.Max(1, curCount);
            int center = (int)Math.Round(frac * innerCnt) % Math.Max(1, innerCnt);
            dest[ni++] = Fetch(innerR, center - 1, out _);
            dest[ni++] = Fetch(innerR, center, out _);
            dest[ni++] = Fetch(innerR, center + 1, out _);
        }

        // Same-ring neighbors: left, right
        dest[ni++] = Fetch(r, p - 1, out _);
        dest[ni++] = Fetch(r, p + 1, out _);

        // Outer ring neighbors
        if (r == _rings - 1)
        {
            if (edgeMode == EdgeMode.WRAP)
            {
                int outerR = 0;
                int outerCnt = _ringCounts[outerR];
                double frac = (double)p / Math.Max(1, curCount);
                int center = (int)Math.Round(frac * outerCnt) % Math.Max(1, outerCnt);
                dest[ni++] = Fetch(outerR, center - 1, out _);
                dest[ni++] = Fetch(outerR, center, out _);
                dest[ni++] = Fetch(outerR, center + 1, out _);
            }
            else { dest[ni++] = backup; dest[ni++] = backup; dest[ni++] = backup; }
        }
        else
        {
            int outerR = r + 1;
            int outerCnt = _ringCounts[outerR];
            double frac = (double)p / Math.Max(1, curCount);
            int center = (int)Math.Round(frac * outerCnt) % Math.Max(1, outerCnt);
            dest[ni++] = Fetch(outerR, center - 1, out _);
            dest[ni++] = Fetch(outerR, center, out _);
            dest[ni++] = Fetch(outerR, center + 1, out _);
        }

        return ni;
    }

    /// <summary>
    /// Produce instance data for rendering or placement helpers. Each Vector4 contains (worldX, worldY, angle, pad).
    /// Cell logical coords (ring,pos) should be passed separately to the renderer.
    /// </summary>
    public Vector4[] GetInstanceData(float cellSize)
    {
        var list = new System.Collections.Generic.List<Vector4>();
        // Use the same backing-grid centered origin as the renderer expects:
        // center = ((count - 1) * cellSize) * 0.5f for each axis so that integer
        // cell indices multiplied by cellSize align with GPU computations.
        var center = GetBackingGridCenter(cellSize);
        for (int r = 0; r < _rings; r++)
        {
            int cnt = _ringCounts[r];
            float radius = r * cellSize;
            // radial padding to avoid overlap near center (decays with radius)
            float radialPad = cellSize * (0.5f + 0.5f * (float)Math.Exp(-radius / (cellSize * 4.0f)));
            float paddedRadius = radius + radialPad;
            for (int p = 0; p < cnt; p++)
            {
                double angle = (2.0 * Math.PI) * ((double)p / cnt);
                float wx = center.X + (float)(paddedRadius * Math.Cos(angle));
                float wy = center.Y + (float)(paddedRadius * Math.Sin(angle));
                // z = angle (radians), w = ring count for this radius
                list.Add(new Vector4(wx, wy, (float)angle, (float)cnt));
            }
        }
        return list.ToArray();
    }

    /// <summary>
    /// Map a world-space coordinate to a logical disk cell (ring,pos). Returns (-1,-1) if outside.
    /// </summary>
    public (int X, int Y) MapWorldToCell(Vector2 worldPos, float cellSize)
    {
        // Mirror the GridVertexDisk/HighlightVertexDisk shader math to pick the
        // instance (ring,pos) that contains the provided world position.
        // Approach: for each instance produced by GetCellWorldPosition, transform
        // the world point into the instance-local space used by the shader and
        // check whether the computed local coordinates fall inside [0,1]^2.

        var center = GetBackingGridCenter(cellSize);
        const float TWOPI = (float)(2.0 * Math.PI);

        int bestR = -1, bestP = -1;
        float bestDistSq = float.MaxValue;

        for (int rr = 0; rr < _rings; rr++)
        {
            int cnt = _ringCounts[rr];
            if (cnt <= 0) continue;

            for (int pp = 0; pp < cnt; pp++)
            {
                var inst = GetCellWorldPosition(rr, pp, cellSize);
                float ix = inst.X;
                float iy = inst.Y;
                double ang = inst.Z - (Math.PI * 0.5); // shader: aInstance.z - PI/2
                float cntf = inst.W;

                float ca = (float)Math.Cos(ang);
                float sa = (float)Math.Sin(ang);

                // world -> instance-local (inverse rotation)
                float dx = worldPos.X - ix;
                float dy = worldPos.Y - iy;
                float localX = ca * dx + sa * dy;
                float localY = -sa * dx + ca * dy;

                // Recompute shader's radial/arc math for this instance
                var centered = new Vector2(ix - center.X, iy - center.Y);
                float radius = centered.Length;
                float desiredRadius = cellSize * (1.0f + cntf / TWOPI);
                float radialPad = desiredRadius;
                float radiusForArc = radius + radialPad;

                float arcOuter = (float)(TWOPI * Math.Max(radiusForArc, 1e-6f) / cntf);
                float arcInner = (float)(TWOPI * Math.Max(radiusForArc - cellSize, 1e-6f) / cntf);
                arcOuter *= 0.98f;
                arcInner *= 0.98f;

                float halfOuter = Math.Max(0.5f, 0.5f * arcOuter);
                float halfInner = Math.Max(0.0f, Math.Min(0.5f * arcInner, halfOuter));

                // In shader: yLocal = (aLocalPos.y - 0.5) * uCellSize + radialPad
                float t = (localY - radialPad) / cellSize + 0.5f;
                if (t < 0.0f || t > 1.0f) continue;

                float halfWidth = (1.0f - t) * halfInner + t * halfOuter; // mix

                float aLocalX = (localX / (2.0f * halfWidth)) + 0.5f;
                if (aLocalX >= 0.0f && aLocalX <= 1.0f)
                {
                    return (rr, pp);
                }

                // Track nearest instance center in case no instance-local hit is found.
                float ddx = worldPos.X - ix;
                float ddy = worldPos.Y - iy;
                float distSq = ddx * ddx + ddy * ddy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestR = rr;
                    bestP = pp;
                }
            }
        }

        // If we found no instance-local hit, but the point lies inside the overall
        // disk extent, return the nearest instance instead. Compute an approximate
        // outer radius from disk center using the outermost ring's first instance.
        bool insideDisk = false;
        if (bestR != -1)
        {
            // find an outer ring instance to approximate disk extent
            int outerRing = -1;
            for (int r = _rings - 1; r >= 0; r--) if (_ringCounts[r] > 0) { outerRing = r; break; }
            if (outerRing >= 0)
            {
                var oi = GetCellWorldPosition(outerRing, 0, cellSize);
                var centerPos = GetBackingGridCenter(cellSize);
                var outerCenter = new Vector2(oi.X, oi.Y);
                float outerDist = (outerCenter - centerPos).Length;
                // include a slack so points near the edge count
                float slack = cellSize * 1.5f;
                float dToCenter = (worldPos - centerPos).Length;
                if (dToCenter <= outerDist + slack) insideDisk = true;
            }
        } else {
            insideDisk = true;
            bestR = 0;
        }

        if (insideDisk && bestR != -1) {
            return (bestR, bestP);
        }

        return (-1, -1);
    }

    /// <summary>
    /// Return the center position of the backing rectangular grid in world coordinates.
    /// This matches what the renderer uses: ((count - 1) * cellSize) * 0.5f per axis.
    /// </summary>
    public Vector2 GetBackingGridCenter(float cellSize)
    {
        return new Vector2(((_rings - 1) * cellSize) * 0.5f, ((_outerCount - 1) * cellSize) * 0.5f);
    }

    /// <summary>
    /// Return the world-space center and angle for the given logical cell (ring,pos).
    /// The returned Vector4 contains (x, y, angle, ringCount) where x/y are centered coordinates
    /// (origin at disk center) suitable for feeding into renderer instance attributes.
    /// </summary>
    public Vector4 GetCellWorldPosition(int ring, int pos, float cellSize)
    {
        if (ring < 0 || ring >= _rings) return new Vector4(0,0,0,0);
        int cnt = _ringCounts[ring];
        if (cnt <= 0) return new Vector4(0,0,0,0);
        float radius = ring * cellSize;
        // same radial padding as GetInstanceData to ensure CPU/GPU alignment
        float radialPad = cellSize * (0.5f + 0.5f * (float)Math.Exp(-radius / (cellSize * 4.0f)));
        float paddedRadius = radius + radialPad;
        double angle = (2.0 * Math.PI) * ((double)pos / cnt);
        var center = GetBackingGridCenter(cellSize);
        float wx = center.X + (float)(paddedRadius * Math.Cos(angle));
        float wy = center.Y + (float)(paddedRadius * Math.Sin(angle));
        return new Vector4(wx, wy, (float)angle, (float)cnt);
    }

    // Provide a simple neighbor coordinates implementation by calling GetNeighbors and mapping back to coords.
    public int GetNeighborCoordinates(int x, int y, EdgeMode edgeMode, Span<int> destX, Span<int> destY)
    {
        //if (destX.Length < 8 || destY.Length < 8) throw new ArgumentException("destX/destY must be at least length 8");

        int ni = 0;
        int r = x;
        int p = y;

        if (r < 0 || r >= _rings) {
            for (int i = 0; i < 8; i++) { destX[ni] = -1; destY[ni] = -1; ni++; }
            return ni;
        }

        int curCount = _ringCounts[r];

        // Helper to resolve a candidate (ring,pos) into a concrete coordinate honoring edgeMode.
        // Returns (resolvedRing, resolvedPos) or (-1,-1) when invalid/out-of-range.
        (int rr, int pp) Resolve(int targetRing, int targetPos, out bool used)
        {
            used = true;

            if (targetRing < 0 || targetRing >= _rings)
            {
                if (edgeMode == EdgeMode.WRAP || edgeMode == EdgeMode.WRAPX || edgeMode == EdgeMode.WRAPY)
                {
                    if (targetRing < 0) targetRing = (targetRing % _rings + _rings) % _rings;
                    else targetRing = targetRing % _rings;
                }
                else
                {
                    used = false;
                    return (-1, -1);
                }
            }

            int cnt = _ringCounts[targetRing];
            if (cnt == 0)
            {
                used = false;
                return (-1, -1);
            }

            int wrapped = ((targetPos % cnt) + cnt) % cnt;
            if (!IsValidCell(targetRing, wrapped))
            {
                used = false;
                return (-1, -1);
            }

            return (targetRing, wrapped);
        }

        // Inner ring neighbors: inner-left, inner-center, inner-right
        if (r == 0)
        {
            if (edgeMode == EdgeMode.WRAP)
            {
                int innerR = _rings - 1;
                int innerCnt = _ringCounts[innerR];
                if (innerCnt > 0)
                {
                    double frac = (double)p / Math.Max(1, curCount);
                    int center = (int) Math.Floor(frac * innerCnt) % innerCnt;
                    var a = Resolve(innerR, center - 1, out _); destX[ni] = a.rr; destY[ni] = a.pp; ni++;
                    var b = Resolve(innerR, center, out _);     destX[ni] = b.rr; destY[ni] = b.pp; ni++;
                    var c = Resolve(innerR, center + 1, out _); destX[ni] = c.rr; destY[ni] = c.pp; ni++;
                }
                else { destX[ni] = -1; destY[ni] = -1; ni++; destX[ni] = -1; destY[ni] = -1; ni++; destX[ni] = -1; destY[ni] = -1; ni++; }
            }
            else { destX[ni] = -1; destY[ni] = -1; ni++; destX[ni] = -1; destY[ni] = -1; ni++; destX[ni] = -1; destY[ni] = -1; ni++; }
        }
        else
        {
            int innerR = r - 1;
            int innerCnt = _ringCounts[innerR];
            double frac = (double)p / Math.Max(1, curCount);
            int center = (int) Math.Floor(frac * innerCnt) % Math.Max(1, innerCnt);
            var a = Resolve(innerR, center - 1, out _); destX[ni] = a.rr; destY[ni] = a.pp; ni++;
            var b = Resolve(innerR, center, out _);     destX[ni] = b.rr; destY[ni] = b.pp; ni++;
            var c = Resolve(innerR, center + 1, out _); destX[ni] = c.rr; destY[ni] = c.pp; ni++;
        }

        // Same-ring neighbors: left, right
        var sL = Resolve(r, p - 1, out _); destX[ni] = sL.rr; destY[ni] = sL.pp; ni++;
        var sR = Resolve(r, p + 1, out _); destX[ni] = sR.rr; destY[ni] = sR.pp; ni++;

        // Outer ring neighbors: outer-left, outer-center, outer-right
        if (r == _rings - 1)
        {
            if (edgeMode == EdgeMode.WRAP)
            {
                int outerR = 0;
                int outerCnt = _ringCounts[outerR];
                double frac = (double)p / Math.Max(1, curCount);
                int center = (int)Math.Round(frac * outerCnt) % Math.Max(1, outerCnt);
                var a = Resolve(outerR, center - 1, out _); destX[ni] = a.rr; destY[ni] = a.pp; ni++;
                var b = Resolve(outerR, center, out _);     destX[ni] = b.rr; destY[ni] = b.pp; ni++;
                var c = Resolve(outerR, center + 1, out _); destX[ni] = c.rr; destY[ni] = c.pp; ni++;
            }
            else { destX[ni] = -1; destY[ni] = -1; ni++; destX[ni] = -1; destY[ni] = -1; ni++; destX[ni] = -1; destY[ni] = -1; ni++; }
        }
        else
        {
            int outerR = r + 1;
            int outerCnt = _ringCounts[outerR];
            double frac = (double)p / Math.Max(1, curCount);
            int center = (int)Math.Round(frac * outerCnt) % Math.Max(1, outerCnt);
            var a = Resolve(outerR, center - 1, out _); destX[ni] = a.rr; destY[ni] = a.pp; ni++;
            var b = Resolve(outerR, center, out _);     destX[ni] = b.rr; destY[ni] = b.pp; ni++;
            var c = Resolve(outerR, center + 1, out _); destX[ni] = c.rr; destY[ni] = c.pp; ni++;
        }

        // Replace any (-1,-1) placeholders for invalid entries with the required sentinel
        for (int i = 0; i < ni; i++) {
            if (destX[i] == -1 || destY[i] == -1) { destX[i] = -1; destY[i] = -1; }
        }

        return ni;
    }

    // Private helpers
    private static int[] ComputeRingCounts(int rings, int outerCount)
    {
        var arr = new int[rings];
        if (rings == 1)
        {
            arr[0] = outerCount;
            return arr;
        }

        for (int r = 0; r < rings; r++)
        {
            // linear interpolation between 3 (minimum) and outerCount
            double t = (double)r / (rings - 1);
            int val = (int)Math.Round(3 + t * (outerCount - 3));
            if (val < 3) val = 3;
            arr[r] = val;
        }

        return arr;
    }
}
