using System;

namespace LiquidSandbox.Simulation;

/// <summary>Grid directions, in the order Combustor uses for its wind (GasSimHud.WindDirections).</summary>
public enum GridDir
{
    Right = 0,
    Up = 1,
    Left = 2,
    Down = 3,
}

/// <summary>
/// Water on Combustor's cell grid. Every cell stores an integer amount of water, where
/// <see cref="Full"/> is one completely filled cell. All movement is done as integer
/// transfers from one cell to another, so the total can only change through
/// <see cref="Add"/>, <see cref="Remove"/> and <see cref="Clear"/>.
///
/// Each fixed tick runs, in order:
///  1. falling: water drops into the neighbouring cell along gravity while it has room;
///  2. spreading: water resting on something flows sideways (perpendicular to gravity),
///     driven by the difference in the height of the water columns, with some momentum;
///  3. pressure: water in a connected body of full cells moves from its highest surface
///     to its lowest opening, so connected vessels reach one level.
/// With zero gravity only an even exchange between neighbouring cells runs.
///
/// The arrays are padded with a one-cell solid border so neighbours never need bounds checks.
/// </summary>
public sealed unsafe class LiquidGrid
{
    public const int Full = 1 << 16;
    public const float MaxStrength = 2f;

    const float FallPassesPerG = 2f;      // cells a free-falling body drops per tick at 1 g
    const int MaxFallPasses = 8;
    const float SpreadGain = 0.2f;        // sideways flow per tick per cell of head difference, at 1 g
    const float SpreadMomentum = 0.9f;    // share of last tick's sideways flow that carries over
    const int FilmMin = Full / 8;         // a resting cell needs this much water to wet a dry neighbour
    const float PressureRate = 0.5f;      // share of a body's imbalance moved per tick, at 1 g
    const float PressureDeadBand = 0.2f;  // level differences below this are left to sideways flow
    const int PressureMinChunk = Full / 16; // smallest pressure transfer, so openings fill completely
    const int Brim = Full - Full / 256;   // a cell this full joins a body of water for pressure
    const float ZeroGExchange = 0.2f;     // share of the difference two neighbours exchange per pass
    const int ZeroGPasses = 3;

    public int Width { get; }
    public int Height { get; }
    /// <summary>Padded row length (Width + 2).</summary>
    public int Stride { get; }
    /// <summary>Padded cell count.</summary>
    public int Length { get; }

    /// <summary>Water per padded cell, 0..Full. Always 0 in solid cells.</summary>
    public readonly int[] Water;
    /// <summary>1 where a padded cell is a wall (including the border).</summary>
    public readonly byte[] Solid;

    /// <summary>Water that could not be placed because there was no room anywhere.</summary>
    public long Overflow { get; private set; }

    public GridDir Gravity { get; set; } = GridDir.Down;

    float _strength = 1f;
    /// <summary>Gravity strength in g, 0..MaxStrength. 0 means no preferred direction.</summary>
    public float Strength
    {
        get => _strength;
        set => _strength = float.IsFinite(value) ? Math.Clamp(value, 0f, MaxStrength) : 0f;
    }

    public long Ticks { get; private set; }

    readonly float[] _qx;    // sideways flow carried between ticks, face (i, i+1)
    readonly float[] _qy;    // face (i, i+Stride)
    readonly float[] _head;
    readonly byte[] _rest;   // 1 where the cell's water is held up by a wall or a resting full cell
    readonly float[] _cellA;
    readonly float[] _cellB;
    readonly int[] _prev;
    readonly int[] _stack;
    readonly int[] _queue;
    readonly int[] _mark;
    int _markValue;
    readonly int[] _srcCell, _srcAvail, _snkCell, _snkRoom;
    readonly float[] _srcLevel, _snkLevel;
    readonly long[] _give;
    float _fallBudget;
    int _axis = -1;

    public LiquidGrid(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Stride = width + 2;
        Length = Stride * (height + 2);
        Water = new int[Length];
        Solid = new byte[Length];
        _qx = new float[Length];
        _qy = new float[Length];
        _head = new float[Length];
        _rest = new byte[Length];
        _cellA = new float[Length];
        _cellB = new float[Length];
        _prev = new int[Length];
        _stack = new int[Length];
        _queue = new int[Length];
        _mark = new int[Length];
        _srcCell = new int[Length];
        _srcAvail = new int[Length];
        _snkCell = new int[Length];
        _snkRoom = new int[Length];
        _srcLevel = new float[Length];
        _snkLevel = new float[Length];
        _give = new long[Length];
        for (int x = 0; x < Stride; x++)
        {
            Solid[x] = 1;
            Solid[Length - Stride + x] = 1;
        }
        for (int y = 0; y < height + 2; y++)
        {
            Solid[y * Stride] = 1;
            Solid[y * Stride + Stride - 1] = 1;
        }
    }

    public int Index(int x, int y) => (y + 1) * Stride + x + 1;

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public int Get(int x, int y) => InBounds(x, y) ? Water[Index(x, y)] : 0;

    public bool IsSolid(int x, int y) => !InBounds(x, y) || Solid[Index(x, y)] != 0;

    public long Total()
    {
        long sum = Overflow;
        int[] w = Water;
        for (int i = 0; i < w.Length; i++) sum += w[i];
        return sum;
    }

    public int Add(int x, int y, int units)
    {
        if (units <= 0 || !InBounds(x, y)) return 0;
        int i = Index(x, y);
        if (Solid[i] != 0) return 0;
        int t = Math.Min(units, Full - Water[i]);
        if (t <= 0) return 0;
        Water[i] += t;
        return t;
    }

    public int Remove(int x, int y, int units)
    {
        if (units <= 0 || !InBounds(x, y)) return 0;
        int i = Index(x, y);
        int t = Math.Min(units, Water[i]);
        Water[i] -= t;
        return t;
    }

    public void Clear()
    {
        Array.Clear(Water);
        Overflow = 0;
        ClearMomentum();
    }

    /// <summary>Copies walls from a row-major (y * Width + x) solid map. Water in a cell that
    /// has become a wall is pushed into the nearest cells with room.</summary>
    public void SyncSolid(ReadOnlySpan<byte> solid)
    {
        if (solid.Length < Width * Height) throw new ArgumentException("Solid map is too small.", nameof(solid));
        int displaced = 0;
        for (int y = 0; y < Height; y++)
        {
            int row = (y + 1) * Stride + 1;
            int src = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int i = row + x;
                if (solid[src + x] != 0)
                {
                    if (Solid[i] != 0) continue;
                    Solid[i] = 1;
                    int w = Water[i];
                    if (w == 0) continue;
                    Water[i] = 0;
                    _stack[displaced] = i;
                    _srcAvail[displaced++] = w;
                }
                else if (Solid[i] != 0)
                {
                    Solid[i] = 0;
                }
            }
        }
        for (int k = 0; k < displaced; k++)
        {
            int left = Deposit(_stack[k], _srcAvail[k]);
            if (left > 0) Overflow += left;
        }
        PlaceOverflow();
    }

    /// <summary>Puts <paramref name="amount"/> into the open cells nearest to <paramref name="origin"/>
    /// (the origin first, if it is open), searching through full cells. Returns what did not fit.</summary>
    int Deposit(int origin, int amount)
    {
        if (amount <= 0) return 0;
        int mark = NextMark();
        int head = 0, tail = 0, stride = Stride;
        if (Solid[origin] == 0)
        {
            _mark[origin] = mark;
            _queue[tail++] = origin;
        }
        else
        {
            _mark[origin] = mark;
            PushOpen(origin + 1, mark, ref tail);
            PushOpen(origin - 1, mark, ref tail);
            PushOpen(origin + stride, mark, ref tail);
            PushOpen(origin - stride, mark, ref tail);
        }
        while (head < tail)
        {
            int c = _queue[head++];
            int room = Full - Water[c];
            if (room > 0)
            {
                int t = Math.Min(room, amount);
                Water[c] += t;
                amount -= t;
                if (amount == 0) break;
            }
            PushOpen(c + 1, mark, ref tail);
            PushOpen(c - 1, mark, ref tail);
            PushOpen(c + stride, mark, ref tail);
            PushOpen(c - stride, mark, ref tail);
        }
        return amount;
    }

    void PushOpen(int c, int mark, ref int tail)
    {
        if (Solid[c] != 0 || _mark[c] == mark) return;
        _mark[c] = mark;
        _queue[tail++] = c;
    }

    int NextMark()
    {
        if (++_markValue == int.MaxValue)
        {
            Array.Clear(_mark);
            _markValue = 1;
        }
        return _markValue;
    }

    void PlaceOverflow()
    {
        if (Overflow <= 0) return;
        for (int i = 0; i < Length && Overflow > 0; i++)
        {
            if (Solid[i] != 0) continue;
            int room = Full - Water[i];
            if (room <= 0) continue;
            int t = (int)Math.Min(room, Overflow);
            Water[i] += t;
            Overflow -= t;
        }
    }

    /// <summary>Moves the water into a grid of another size. Cell (x, y) lands at (x + dx, y + dy).
    /// Water that falls outside, or into a wall, is placed in the nearest cells with room.</summary>
    public LiquidGrid Resized(int width, int height, int dx, int dy, ReadOnlySpan<byte> solid)
    {
        var next = new LiquidGrid(width, height) { Gravity = Gravity, Strength = Strength };
        next.SyncSolid(solid);
        next.Overflow = Overflow;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int w = Water[Index(x, y)];
                if (w == 0) continue;
                int nx = Math.Clamp(x + dx, 0, width - 1);
                int ny = Math.Clamp(y + dy, 0, height - 1);
                int left = next.Deposit(next.Index(nx, ny), w);
                if (left > 0) next.Overflow += left;
            }
        }
        next.PlaceOverflow();
        return next;
    }

    /// <summary>Water above this cell along gravity, in cells, counting the cell itself.</summary>
    public float DepthAt(int x, int y)
    {
        if (!InBounds(x, y)) return 0f;
        int i = Index(x, y);
        if (Water[i] == 0) return 0f;
        int up = -DownOffset();
        float depth = 0f;
        for (int c = i; Solid[c] == 0 && Water[c] > 0; c += up)
        {
            depth += (float)Water[c] / Full;
            if (Water[c] < Full) break;
        }
        return depth;
    }

    public int DownOffset() => Gravity switch
    {
        GridDir.Right => 1,
        GridDir.Up => Stride,
        GridDir.Left => -1,
        _ => -Stride,
    };

    void ClearMomentum()
    {
        Array.Clear(_qx);
        Array.Clear(_qy);
    }

    /// <summary>Advances one fixed tick.</summary>
    public void Step()
    {
        Ticks++;
        float s = _strength;
        if (!(s > 0f))
        {
            if (_axis != -1)
            {
                ClearMomentum();
                _axis = -1;
            }
            _fallBudget = 0f;
            for (int p = 0; p < ZeroGPasses; p++) Exchange();
            return;
        }

        int axis = Gravity is GridDir.Up or GridDir.Down ? 0 : 1;
        if (axis != _axis)
        {
            ClearMomentum();
            _axis = axis;
        }

        _fallBudget += s * FallPassesPerG;
        int passes = (int)_fallBudget;
        _fallBudget -= passes;
        if (passes > MaxFallPasses) passes = MaxFallPasses;
        for (int p = 0; p < passes; p++) Fall();
        Spread(s);
        Pressure(s);
    }

    // ---------------------------------------------------------------- falling

    void Fall()
    {
        int w = Width, h = Height, stride = Stride;
        fixed (int* water = Water)
        fixed (byte* solid = Solid)
        {
            switch (Gravity)
            {
                case GridDir.Down:
                    for (int y = 2; y <= h; y++)
                    {
                        int row = y * stride;
                        for (int i = row + 1, end = row + w; i <= end; i++) FallCell(water, solid, i, i - stride);
                    }
                    break;
                case GridDir.Up:
                    for (int y = h - 1; y >= 1; y--)
                    {
                        int row = y * stride;
                        for (int i = row + 1, end = row + w; i <= end; i++) FallCell(water, solid, i, i + stride);
                    }
                    break;
                case GridDir.Left:
                    for (int x = 2; x <= w; x++)
                        for (int i = stride + x, end = h * stride + x; i <= end; i += stride) FallCell(water, solid, i, i - 1);
                    break;
                default:
                    for (int x = w - 1; x >= 1; x--)
                        for (int i = stride + x, end = h * stride + x; i <= end; i += stride) FallCell(water, solid, i, i + 1);
                    break;
            }
        }
    }

    static void FallCell(int* water, byte* solid, int i, int j)
    {
        int wi = water[i];
        if (wi == 0 || solid[j] != 0) return;
        int room = Full - water[j];
        if (room <= 0) return;
        int t = wi < room ? wi : room;
        water[i] = wi - t;
        water[j] += t;
    }

    // ---------------------------------------------------------------- spreading

    /// <summary>Head = height of the water surface of the column a cell belongs to, measured
    /// against gravity in cells. Empty cells get the height of their own floor.</summary>
    void ComputeHeads(int* water, byte* solid, float* head)
    {
        int w = Width, h = Height, stride = Stride;
        switch (Gravity)
        {
            case GridDir.Down:
                for (int x = 1; x <= w; x++)
                {
                    float run = -1f;
                    for (int y = h; y >= 1; y--) HeadCell(water, solid, head, y * stride + x, y, ref run);
                }
                break;
            case GridDir.Up:
                for (int x = 1; x <= w; x++)
                {
                    float run = -1f;
                    for (int y = 1; y <= h; y++) HeadCell(water, solid, head, y * stride + x, h + 1 - y, ref run);
                }
                break;
            case GridDir.Left:
                for (int y = 1; y <= h; y++)
                {
                    float run = -1f;
                    int row = y * stride;
                    for (int x = w; x >= 1; x--) HeadCell(water, solid, head, row + x, x, ref run);
                }
                break;
            default:
                for (int y = 1; y <= h; y++)
                {
                    float run = -1f;
                    int row = y * stride;
                    for (int x = 1; x <= w; x++) HeadCell(water, solid, head, row + x, w + 1 - x, ref run);
                }
                break;
        }
    }

    static void HeadCell(int* water, byte* solid, float* head, int i, int z, ref float run)
    {
        if (solid[i] != 0)
        {
            run = -1f;
            return;
        }
        int wi = water[i];
        if (wi == 0)
        {
            head[i] = z;
            run = -1f;
            return;
        }
        if (run < 0f) run = z + (float)wi / Full;
        head[i] = run;
    }

    /// <summary>Marks cells whose water is held up: the next cell along gravity is a wall, or is
    /// full and itself held up. Falling water is not held up, so it does not spread or press.</summary>
    void ComputeRest(int* water, byte* solid, byte* rest)
    {
        int w = Width, h = Height, stride = Stride;
        switch (Gravity)
        {
            case GridDir.Down:
                for (int x = 1; x <= w; x++)
                {
                    byte held = 1;
                    for (int y = 1; y <= h; y++) RestCell(water, solid, rest, y * stride + x, ref held);
                }
                break;
            case GridDir.Up:
                for (int x = 1; x <= w; x++)
                {
                    byte held = 1;
                    for (int y = h; y >= 1; y--) RestCell(water, solid, rest, y * stride + x, ref held);
                }
                break;
            case GridDir.Left:
                for (int y = 1; y <= h; y++)
                {
                    byte held = 1;
                    int row = y * stride;
                    for (int x = 1; x <= w; x++) RestCell(water, solid, rest, row + x, ref held);
                }
                break;
            default:
                for (int y = 1; y <= h; y++)
                {
                    byte held = 1;
                    int row = y * stride;
                    for (int x = w; x >= 1; x--) RestCell(water, solid, rest, row + x, ref held);
                }
                break;
        }
    }

    static void RestCell(int* water, byte* solid, byte* rest, int i, ref byte held)
    {
        rest[i] = held;
        held = solid[i] != 0 ? (byte)1 : water[i] >= Full ? held : (byte)0;
    }

    void Spread(float strength)
    {
        int lat = Gravity is GridDir.Up or GridDir.Down ? 1 : Stride;
        float gain = SpreadGain * strength * Full;
        int first = Stride, last = Length - Stride - 1 - lat;
        fixed (int* water = Water)
        fixed (byte* solid = Solid)
        fixed (float* head = _head)
        fixed (byte* rest = _rest)
        fixed (float* q = lat == 1 ? _qx : _qy)
        fixed (float* outScale = _cellA)
        fixed (float* inScale = _cellB)
        {
            ComputeHeads(water, solid, head);
            ComputeRest(water, solid, rest);
            new Span<float>(outScale, Length).Clear();
            new Span<float>(inScale, Length).Clear();

            // Desired flow through each sideways face, with momentum.
            for (int i = first; i <= last; i++)
            {
                int j = i + lat;
                if (solid[i] != 0 || solid[j] != 0)
                {
                    q[i] = 0f;
                    continue;
                }
                int wi = water[i], wj = water[j];
                if ((wi | wj) == 0)
                {
                    q[i] = 0f;
                    continue;
                }
                float flow = q[i] * SpreadMomentum + gain * (head[i] - head[j]);
                if (flow > 0f)
                {
                    if (wi == 0 || rest[i] == 0 || (wj == 0 && wi < FilmMin)) flow = 0f;
                    else outScale[i] += flow;
                }
                else if (flow < 0f)
                {
                    if (wj == 0 || rest[j] == 0 || (wi == 0 && wj < FilmMin)) flow = 0f;
                    else outScale[j] -= flow;
                }
                q[i] = flow;
            }

            // No cell may send more than it holds.
            for (int i = first; i <= last + lat; i++)
            {
                float o = outScale[i];
                outScale[i] = o > water[i] ? water[i] / o : 1f;
            }
            for (int i = first; i <= last; i++)
            {
                float flow = q[i];
                if (flow > 0f)
                {
                    flow *= outScale[i];
                    inScale[i + lat] += flow;
                }
                else if (flow < 0f)
                {
                    flow *= outScale[i + lat];
                    inScale[i] -= flow;
                }
                q[i] = flow;
            }

            // No cell may receive more than it has room for.
            for (int i = first; i <= last + lat; i++)
            {
                float n = inScale[i];
                float room = Full - water[i];
                inScale[i] = n > room ? room / n : 1f;
            }
            for (int i = first; i <= last; i++)
            {
                float flow = q[i];
                if (flow == 0f) continue;
                int j = i + lat;
                int t;
                if (flow > 0f)
                {
                    t = (int)(flow * inScale[j]);
                    water[i] -= t;
                    water[j] += t;
                }
                else
                {
                    t = (int)(flow * inScale[i]);
                    water[i] -= t;
                    water[j] += t;
                }
                q[i] = t;
            }
        }
    }

    // ---------------------------------------------------------------- pressure

    int Elevation(int i)
    {
        int y = i / Stride, x = i - y * Stride;
        return Gravity switch
        {
            GridDir.Down => y,
            GridDir.Up => Height + 1 - y,
            GridDir.Left => x,
            _ => Width + 1 - x,
        };
    }

    void Pressure(float strength)
    {
        float rate = Math.Min(1f, PressureRate * strength);
        int down = DownOffset(), stride = Stride;
        int visited = NextMark();
        int[] water = Water;
        byte[] solid = Solid;
        for (int start = stride; start < Length - stride; start++)
        {
            if (water[start] < Brim || _mark[start] == visited) continue;

            int sinkMark = NextMark();
            int sp = 0, nSrc = 0, nSnk = 0;
            bool held = false;
            float maxSrc = float.MinValue, minSnk = float.MaxValue;
            _mark[start] = visited;
            _stack[sp++] = start;
            while (sp > 0)
            {
                int c = _stack[--sp];
                if (_rest[c] != 0) held = true;
                int up = c - down;
                int wu = water[up];
                if (solid[up] != 0 || wu < Brim)
                {
                    // Top of a column: this cell can give, and so can partial water resting on it.
                    float lvl = Elevation(c) + (float)water[c] / Full;
                    _srcCell[nSrc] = c;
                    _srcAvail[nSrc] = water[c];
                    _srcLevel[nSrc++] = lvl;
                    if (lvl > maxSrc) maxSrc = lvl;
                    if (solid[up] == 0 && wu > 0)
                    {
                        lvl = Elevation(up) + (float)wu / Full;
                        _srcCell[nSrc] = up;
                        _srcAvail[nSrc] = wu;
                        _srcLevel[nSrc++] = lvl;
                        if (lvl > maxSrc) maxSrc = lvl;
                    }
                }
                Visit(c + 1, visited, sinkMark, ref sp, ref nSnk, ref minSnk);
                Visit(c - 1, visited, sinkMark, ref sp, ref nSnk, ref minSnk);
                Visit(c + stride, visited, sinkMark, ref sp, ref nSnk, ref minSnk);
                Visit(c - stride, visited, sinkMark, ref sp, ref nSnk, ref minSnk);
            }
            if (!held || nSrc == 0 || nSnk == 0 || maxSrc - minSnk <= PressureDeadBand) continue;
            Level(nSrc, nSnk, minSnk, maxSrc, rate);
        }
    }

    void Visit(int n, int visited, int sinkMark, ref int sp, ref int nSnk, ref float minSnk)
    {
        if (Solid[n] != 0) return;
        int wn = Water[n];
        if (wn >= Brim)
        {
            if (_mark[n] == visited) return;
            _mark[n] = visited;
            _stack[sp++] = n;
            return;
        }
        if (_mark[n] == sinkMark) return;
        _mark[n] = sinkMark;
        float lvl = Elevation(n) + (float)wn / Full;
        _snkCell[nSnk] = n;
        _snkRoom[nSnk] = Full - wn;
        _snkLevel[nSnk++] = lvl;
        if (lvl < minSnk) minSnk = lvl;
    }

    /// <summary>Moves water from the sources above a common level to the openings below it.</summary>
    void Level(int nSrc, int nSnk, float lo, float hi, float rate)
    {
        float band = PressureDeadBand * 0.5f;
        for (int it = 0; it < 30; it++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Above(mid + band, nSrc) > Below(mid - band, nSnk)) lo = mid;
            else hi = mid;
        }
        float level = (lo + hi) * 0.5f;
        double give = Above(level + band, nSrc), take = Below(level - band, nSnk);
        double most = Math.Min(give, take);
        double move = Math.Min(most, Math.Max(most * rate, PressureMinChunk));
        if (move < 1.0) return;

        long given = 0;
        double fGive = move / give;
        for (int k = 0; k < nSrc; k++)
        {
            double share = Math.Min(_srcAvail[k], (double)Full * Math.Max(0f, _srcLevel[k] - level - band));
            long g = Math.Min(_srcAvail[k], (long)Math.Ceiling(share * fGive));
            _give[k] = g;
            given += g;
        }
        if (given == 0) return;

        long placed = 0;
        double fTake = given / take;
        for (int k = 0; k < nSnk; k++)
        {
            double share = Math.Min(_snkRoom[k], (double)Full * Math.Max(0f, level - band - _snkLevel[k]));
            int t = (int)Math.Min(_snkRoom[k], (long)(share * fTake));
            _snkRoom[k] -= t;
            Water[_snkCell[k]] += t;
            placed += t;
        }
        for (int k = 0; k < nSnk && placed < given; k++)
        {
            int t = (int)Math.Min(_snkRoom[k], given - placed);
            if (t <= 0) continue;
            _snkRoom[k] -= t;
            Water[_snkCell[k]] += t;
            placed += t;
        }
        // Take exactly what was placed from the sources.
        long remaining = placed;
        for (int k = 0; k < nSrc && remaining > 0; k++)
        {
            long g = Math.Min(_give[k], remaining);
            Water[_srcCell[k]] -= (int)g;
            remaining -= g;
        }
    }

    double Above(float level, int nSrc)
    {
        double sum = 0;
        for (int k = 0; k < nSrc; k++)
        {
            float d = _srcLevel[k] - level;
            if (d > 0f) sum += Math.Min(_srcAvail[k], (double)Full * d);
        }
        return sum;
    }

    double Below(float level, int nSnk)
    {
        double sum = 0;
        for (int k = 0; k < nSnk; k++)
        {
            float d = level - _snkLevel[k];
            if (d > 0f) sum += Math.Min(_snkRoom[k], (double)Full * d);
        }
        return sum;
    }

    // ---------------------------------------------------------------- zero gravity

    /// <summary>Neighbouring cells exchange a share of their difference. Uses the state at the
    /// start of the pass, so the result does not depend on scan order.</summary>
    void Exchange()
    {
        Array.Copy(Water, _prev, Length);
        int stride = Stride, first = stride, last = Length - stride - 1;
        fixed (int* water = Water)
        fixed (int* prev = _prev)
        fixed (byte* solid = Solid)
        {
            for (int i = first; i <= last; i++)
            {
                if (solid[i] != 0) continue;
                int wi = prev[i];
                int j = i + 1;
                if (solid[j] == 0)
                {
                    int t = (int)((wi - prev[j]) * ZeroGExchange);
                    water[i] -= t;
                    water[j] += t;
                }
                j = i + stride;
                if (solid[j] == 0)
                {
                    int t = (int)((wi - prev[j]) * ZeroGExchange);
                    water[i] -= t;
                    water[j] += t;
                }
            }
        }
    }
}
