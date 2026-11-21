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
/// Liquids on Combustor's cell grid. Every cell holds an integer amount of one liquid (or none),
/// where <see cref="Full"/> is one completely filled cell, plus vapour of each liquid in empty cells.
/// All movement is done as integer transfers, so for every liquid the sum of its liquid, its vapour
/// and its overflow can only change through <see cref="Add"/>, <see cref="Remove"/> and <see cref="Clear"/>.
///
/// Each fixed tick runs, in order:
///  1. falling: liquid drops along gravity while there is room; a denser liquid trades places with a
///     lighter one below it, so fuels float on water;
///  2. spreading: liquid resting on something flows sideways towards lower pressure, with momentum;
///     falling liquid keeps its sideways speed, so water leaving a hole flies as a jet;
///  3. pressure: a connected body of full cells moves liquid from its highest surface to its lowest
///     openings, which levels connected vessels and makes deep openings spout fast;
///  4. evaporation: exposed surfaces evaporate into the empty cell above until that air is saturated;
///     vapour spreads through empty cells.
/// With zero gravity, neighbouring cells of the same liquid exchange until they are even.
/// The arrays are padded with a one-cell solid border so neighbours never need bounds checks.
/// </summary>
public sealed unsafe class LiquidGrid
{
    public const int Full = 1 << 16;
    public const float MaxStrength = 2f;
    public const int KindCount = 3;
    public const byte Water1 = 1, Gasoline = 2, Diesel = 3;

    public static readonly string[] Names = { "", "Water", "Gasoline", "Diesel" };
    /// <summary>Density relative to water.</summary>
    public static readonly float[] Density = { 1f, 1f, 0.74f, 0.84f };
    /// <summary>How readily each liquid flows sideways (water = 1).</summary>
    static readonly float[] Fluidity = { 1f, 1f, 1.25f, 0.55f };
    /// <summary>Units per tick an exposed cell evaporates into dry air at 1 g.</summary>
    static readonly float[] EvaporationRate = { 0f, 1.2f, 26f, 0.6f };
    /// <summary>Vapour one empty cell holds before evaporation into it stops.</summary>
    public static readonly int[] Saturation = { 1, Full / 200, Full / 40, Full / 400 };

    const float FallPassesPerG = 2f;
    const int MaxFallPasses = 8;
    const float SpreadGain = 0.2f;
    const float SpreadMomentum = 0.9f;
    const int FilmMin = Full / 8;
    const float PressureRate = 0.5f;
    const float PressureDeadBand = 0.2f;
    const int PressureMinChunk = Full / 16;
    const int Brim = Full - Full / 256;
    const float JetGain = 0.25f;          // exit speed (cells per tick) per sqrt(cell of head * g)
    const float MaxJet = 1f;
    const float AirDrag = 0.97f;          // sideways speed kept per tick while falling
    const float ExchangeShare = 0.2f;
    const int ZeroGPasses = 3;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int Length { get; }

    /// <summary>Liquid per padded cell, 0..Full. Always 0 in solid cells.</summary>
    public readonly int[] Water;
    /// <summary>Which liquid a padded cell holds (0 when empty).</summary>
    public readonly byte[] Kind;
    /// <summary>1 where a padded cell is a wall (including the border).</summary>
    public readonly byte[] Solid;
    /// <summary>Vapour per liquid kind (index 1..3), held in empty cells.</summary>
    public readonly int[][] Vapor;
    /// <summary>Liquid per kind that could not be placed because there was no room.</summary>
    public readonly long[] Overflow = new long[KindCount + 1];

    public GridDir Gravity { get; set; } = GridDir.Down;
    public bool Evaporation { get; set; } = true;

    float _strength = 1f;
    public float Strength
    {
        get => _strength;
        set => _strength = float.IsFinite(value) ? Math.Clamp(value, 0f, MaxStrength) : 0f;
    }

    public long Ticks { get; private set; }

    readonly float[] _qx, _qy, _head, _vel, _cellA, _cellB;
    readonly byte[] _rest;
    readonly int[] _prev, _stack, _queue, _mark;
    int _markValue;
    readonly int[] _srcCell, _srcAvail, _snkCell, _snkRoom, _snkSide;
    readonly float[] _srcLevel, _snkLevel;
    readonly long[] _give;
    readonly bool[] _vaporPresent = new bool[KindCount + 1];
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
        Kind = new byte[Length];
        Solid = new byte[Length];
        Vapor = new int[KindCount + 1][];
        for (int k = 1; k <= KindCount; k++) Vapor[k] = new int[Length];
        _qx = new float[Length];
        _qy = new float[Length];
        _head = new float[Length];
        _vel = new float[Length];
        _cellA = new float[Length];
        _cellB = new float[Length];
        _rest = new byte[Length];
        _prev = new int[Length];
        _stack = new int[Length];
        _queue = new int[Length];
        _mark = new int[Length];
        _srcCell = new int[Length];
        _srcAvail = new int[Length];
        _snkCell = new int[Length];
        _snkRoom = new int[Length];
        _snkSide = new int[Length];
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
    public int KindAt(int x, int y) => InBounds(x, y) ? Kind[Index(x, y)] : 0;
    public int VaporAt(int x, int y, int kind) => InBounds(x, y) ? Vapor[kind][Index(x, y)] : 0;
    public bool IsSolid(int x, int y) => !InBounds(x, y) || Solid[Index(x, y)] != 0;
    /// <summary>True when the padded cell's liquid is held up rather than falling (as of the last tick).</summary>
    public bool Rests(int i) => _rest[i] != 0;

    /// <summary>All liquid, vapour and overflow of one kind, in units.</summary>
    public long Total(int kind)
    {
        long sum = Overflow[kind];
        int[] w = Water, v = Vapor[kind];
        byte[] k = Kind;
        for (int i = 0; i < w.Length; i++)
        {
            if (k[i] == kind) sum += w[i];
            sum += v[i];
        }
        return sum;
    }

    public long Total()
    {
        long sum = 0;
        for (int k = 1; k <= KindCount; k++) sum += Total(k);
        return sum;
    }

    public long LiquidOf(int kind)
    {
        long sum = 0;
        for (int i = 0; i < Water.Length; i++) if (Kind[i] == kind) sum += Water[i];
        return sum;
    }

    public long VaporOf(int kind)
    {
        long sum = 0;
        int[] v = Vapor[kind];
        for (int i = 0; i < v.Length; i++) sum += v[i];
        return sum;
    }

    public int Add(int x, int y, int units, int kind = Water1)
    {
        if (units <= 0 || kind < 1 || kind > KindCount || !InBounds(x, y)) return 0;
        int i = Index(x, y);
        if (Solid[i] != 0 || (Kind[i] != 0 && Kind[i] != kind)) return 0;
        int t = Math.Min(units, Full - Water[i]);
        if (t <= 0) return 0;
        Water[i] += t;
        Kind[i] = (byte)kind;
        return t;
    }

    public int Remove(int x, int y, int units)
    {
        if (units <= 0 || !InBounds(x, y)) return 0;
        int i = Index(x, y);
        int t = Math.Min(units, Water[i]);
        Water[i] -= t;
        if (Water[i] == 0) Empty(i);
        return t;
    }

    void Empty(int i)
    {
        Kind[i] = 0;
        _vel[i] = 0f;
    }

    public void Clear()
    {
        Array.Clear(Water);
        Array.Clear(Kind);
        for (int k = 1; k <= KindCount; k++) Array.Clear(Vapor[k]);
        Array.Clear(Overflow);
        Array.Clear(_vaporPresent);
        Array.Clear(_vel);
        ClearMomentum();
    }

    // ---------------------------------------------------------------- walls

    /// <summary>Copies walls from a row-major (y * Width + x) solid map. Liquid and vapour in a cell
    /// that has become a wall are pushed into the nearest open cells.</summary>
    public void SyncSolid(ReadOnlySpan<byte> solid)
    {
        if (solid.Length < Width * Height) throw new ArgumentException("Solid map is too small.", nameof(solid));
        int displaced = 0;
        for (int y = 0; y < Height; y++)
        {
            int row = (y + 1) * Stride + 1, src = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int i = row + x;
                if (solid[src + x] != 0)
                {
                    if (Solid[i] != 0) continue;
                    Solid[i] = 1;
                    _stack[displaced++] = i;
                }
                else if (Solid[i] != 0) Solid[i] = 0;
            }
        }
        for (int d = 0; d < displaced; d++)
        {
            int i = _stack[d];
            int w = Water[i], kind = Kind[i];
            Water[i] = 0;
            Empty(i);
            if (w > 0)
            {
                int left = Deposit(i, w, kind);
                if (left > 0) Overflow[kind] += left;
            }
            for (int k = 1; k <= KindCount; k++)
            {
                int v = Vapor[k][i];
                if (v == 0) continue;
                Vapor[k][i] = 0;
                int air = NearestAir(i);
                if (air >= 0) { Vapor[k][air] += v; _vaporPresent[k] = true; }
                else Overflow[k] += v;
            }
        }
        PlaceOverflow();
    }

    /// <summary>Puts liquid into the open cells nearest to <paramref name="origin"/> that are empty or hold
    /// the same liquid (the origin first, if it is open), searching through any open cells.</summary>
    int Deposit(int origin, int amount, int kind)
    {
        if (amount <= 0) return 0;
        int mark = NextMark();
        int head = 0, tail = 0, stride = Stride;
        _mark[origin] = mark;
        if (Solid[origin] == 0) _queue[tail++] = origin;
        else
        {
            PushOpen(origin + 1, mark, ref tail);
            PushOpen(origin - 1, mark, ref tail);
            PushOpen(origin + stride, mark, ref tail);
            PushOpen(origin - stride, mark, ref tail);
        }
        while (head < tail)
        {
            int c = _queue[head++];
            if (Kind[c] == 0 || Kind[c] == kind)
            {
                int room = Full - Water[c];
                if (room > 0)
                {
                    int t = Math.Min(room, amount);
                    Water[c] += t;
                    Kind[c] = (byte)kind;
                    amount -= t;
                    if (amount == 0) break;
                }
            }
            PushOpen(c + 1, mark, ref tail);
            PushOpen(c - 1, mark, ref tail);
            PushOpen(c + stride, mark, ref tail);
            PushOpen(c - stride, mark, ref tail);
        }
        return amount;
    }

    int NearestAir(int origin)
    {
        int mark = NextMark();
        int head = 0, tail = 0, stride = Stride;
        _mark[origin] = mark;
        PushOpen(origin + 1, mark, ref tail);
        PushOpen(origin - 1, mark, ref tail);
        PushOpen(origin + stride, mark, ref tail);
        PushOpen(origin - stride, mark, ref tail);
        while (head < tail)
        {
            int c = _queue[head++];
            if (Kind[c] == 0) return c;
            PushOpen(c + 1, mark, ref tail);
            PushOpen(c - 1, mark, ref tail);
            PushOpen(c + stride, mark, ref tail);
            PushOpen(c - stride, mark, ref tail);
        }
        return -1;
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
        for (int k = 1; k <= KindCount; k++)
        {
            for (int i = 0; i < Length && Overflow[k] > 0; i++)
            {
                if (Solid[i] != 0 || (Kind[i] != 0 && Kind[i] != k)) continue;
                int room = Full - Water[i];
                if (room <= 0) continue;
                int t = (int)Math.Min(room, Overflow[k]);
                Water[i] += t;
                Kind[i] = (byte)k;
                Overflow[k] -= t;
            }
        }
    }

    /// <summary>Moves everything into a grid of another size. Cell (x, y) lands at (x + dx, y + dy).</summary>
    public LiquidGrid Resized(int width, int height, int dx, int dy, ReadOnlySpan<byte> solid)
    {
        var next = new LiquidGrid(width, height) { Gravity = Gravity, Strength = Strength, Evaporation = Evaporation };
        next.SyncSolid(solid);
        for (int k = 1; k <= KindCount; k++) next.Overflow[k] = Overflow[k];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = Index(x, y);
                int nx = Math.Clamp(x + dx, 0, width - 1), ny = Math.Clamp(y + dy, 0, height - 1);
                int ni = next.Index(nx, ny);
                if (Water[i] > 0)
                {
                    int left = next.Deposit(ni, Water[i], Kind[i]);
                    if (left > 0) next.Overflow[Kind[i]] += left;
                }
                for (int k = 1; k <= KindCount; k++)
                {
                    int v = Vapor[k][i];
                    if (v == 0) continue;
                    int air = next.Solid[ni] == 0 && next.Kind[ni] == 0 ? ni : next.NearestAir(ni);
                    if (air >= 0) { next.Vapor[k][air] += v; next._vaporPresent[k] = true; }
                    else next.Overflow[k] += v;
                }
            }
        }
        next.PlaceOverflow();
        return next;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Liquid above this cell along gravity, in cells, counting the cell itself.</summary>
    public float DepthAt(int x, int y)
    {
        if (!InBounds(x, y)) return 0f;
        int up = -DownOffset();
        float depth = 0f;
        for (int c = Index(x, y); Solid[c] == 0 && Water[c] > 0; c += up)
        {
            depth += (float)Water[c] / Full;
            if (Water[c] < Full) break;
        }
        return depth;
    }

    /// <summary>Hydrostatic pressure at this cell as a head of water, in cells (density-weighted depth
    /// times gravity strength).</summary>
    public float PressureAt(int x, int y)
    {
        if (!InBounds(x, y)) return 0f;
        int up = -DownOffset();
        float head = 0f;
        for (int c = Index(x, y); Solid[c] == 0 && Water[c] > 0; c += up)
        {
            head += Density[Kind[c]] * Water[c] / Full;
            if (Water[c] < Full) break;
        }
        return head * _strength;
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
        Array.Clear(_vel);
    }

    // ---------------------------------------------------------------- tick

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
            if (Evaporation) Evaporate(0f);
            SpreadVapor();
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
        Coast();
        Pressure(s);
        if (Evaporation) Evaporate(s);
        SpreadVapor();
    }

    // ---------------------------------------------------------------- falling

    void Fall()
    {
        int w = Width, h = Height, stride = Stride;
        fixed (int* water = Water)
        fixed (byte* kind = Kind)
        fixed (byte* solid = Solid)
        fixed (float* vel = _vel)
        fixed (float* density = Density)
        {
            switch (Gravity)
            {
                case GridDir.Down:
                    for (int y = 2; y <= h; y++)
                        for (int i = y * stride + 1, end = y * stride + w; i <= end; i++) FallCell(water, kind, solid, vel, density, i, i - stride);
                    break;
                case GridDir.Up:
                    for (int y = h - 1; y >= 1; y--)
                        for (int i = y * stride + 1, end = y * stride + w; i <= end; i++) FallCell(water, kind, solid, vel, density, i, i + stride);
                    break;
                case GridDir.Left:
                    for (int x = 2; x <= w; x++)
                        for (int i = stride + x, end = h * stride + x; i <= end; i += stride) FallCell(water, kind, solid, vel, density, i, i - 1);
                    break;
                default:
                    for (int x = w - 1; x >= 1; x--)
                        for (int i = stride + x, end = h * stride + x; i <= end; i += stride) FallCell(water, kind, solid, vel, density, i, i + 1);
                    break;
            }
        }
    }

    static void FallCell(int* water, byte* kind, byte* solid, float* vel, float* density, int i, int j)
    {
        int wi = water[i];
        if (wi == 0 || solid[j] != 0) return;
        byte ki = kind[i], kj = kind[j];
        if (kj == 0 || kj == ki)
        {
            int wj = water[j];
            int room = Full - wj;
            if (room <= 0) return;
            int t = wi < room ? wi : room;
            vel[j] = (vel[j] * wj + vel[i] * t) / (wj + t);
            water[j] = wj + t;
            kind[j] = ki;
            water[i] = wi - t;
            if (water[i] == 0)
            {
                kind[i] = 0;
                vel[i] = 0f;
            }
        }
        else if (density[ki] > density[kj])
        {
            // The heavier liquid sinks through the lighter one by trading places with it.
            water[i] = water[j];
            water[j] = wi;
            kind[i] = kj;
            kind[j] = ki;
            (vel[i], vel[j]) = (vel[j], vel[i]);
        }
    }

    // ---------------------------------------------------------------- spreading

    /// <summary>Head of each cell: its height against gravity plus the density-weighted depth of the
    /// liquid above it (the pressure there, as a head of water). Empty cells get their floor height.</summary>
    void ComputeHeads(int* water, byte* kind, byte* solid, float* head, float* density)
    {
        int w = Width, h = Height, stride = Stride;
        switch (Gravity)
        {
            case GridDir.Down:
                for (int x = 1; x <= w; x++)
                {
                    float p = 0f;
                    for (int y = h; y >= 1; y--) HeadCell(water, kind, solid, head, density, y * stride + x, y, ref p);
                }
                break;
            case GridDir.Up:
                for (int x = 1; x <= w; x++)
                {
                    float p = 0f;
                    for (int y = 1; y <= h; y++) HeadCell(water, kind, solid, head, density, y * stride + x, h + 1 - y, ref p);
                }
                break;
            case GridDir.Left:
                for (int y = 1; y <= h; y++)
                {
                    float p = 0f;
                    for (int x = w; x >= 1; x--) HeadCell(water, kind, solid, head, density, y * stride + x, x, ref p);
                }
                break;
            default:
                for (int y = 1; y <= h; y++)
                {
                    float p = 0f;
                    for (int x = 1; x <= w; x++) HeadCell(water, kind, solid, head, density, y * stride + x, w + 1 - x, ref p);
                }
                break;
        }
    }

    static void HeadCell(int* water, byte* kind, byte* solid, float* head, float* density, int i, int z, ref float p)
    {
        if (solid[i] != 0)
        {
            p = 0f;
            return;
        }
        int wi = water[i];
        if (wi == 0)
        {
            head[i] = z;
            p = 0f;
            return;
        }
        p += density[kind[i]] * wi / Full;
        head[i] = z + p;
    }

    void ComputeRest(int* water, byte* kind, byte* solid, byte* rest)
    {
        int w = Width, h = Height, stride = Stride;
        switch (Gravity)
        {
            case GridDir.Down:
                for (int x = 1; x <= w; x++) { byte held = 1; for (int y = 1; y <= h; y++) RestCell(water, kind, solid, rest, y * stride + x, stride, ref held); }
                break;
            case GridDir.Up:
                for (int x = 1; x <= w; x++) { byte held = 1; for (int y = h; y >= 1; y--) RestCell(water, kind, solid, rest, y * stride + x, -stride, ref held); }
                break;
            case GridDir.Left:
                for (int y = 1; y <= h; y++) { byte held = 1; for (int x = 1; x <= w; x++) RestCell(water, kind, solid, rest, y * stride + x, 1, ref held); }
                break;
            default:
                for (int y = 1; y <= h; y++) { byte held = 1; for (int x = w; x >= 1; x--) RestCell(water, kind, solid, rest, y * stride + x, -1, ref held); }
                break;
        }
    }

    /// <summary>A cell is held up by a wall, by full liquid that is held, or by another liquid it cannot
    /// sink into (fuel on water) that is held.</summary>
    static void RestCell(int* water, byte* kind, byte* solid, byte* rest, int i, int up, ref byte held)
    {
        rest[i] = held;
        if (solid[i] != 0) { held = 1; return; }
        int wi = water[i];
        byte above = kind[i + up];
        bool supports = wi >= Full || (wi > 0 && above != 0 && above != kind[i]);
        held = supports ? held : (byte)0;
    }

    void Spread(float strength)
    {
        int lat = Gravity is GridDir.Up or GridDir.Down ? 1 : Stride;
        float gain = SpreadGain * strength * Full;
        int first = Stride, last = Length - Stride - 1 - lat;
        fixed (int* water = Water)
        fixed (byte* kind = Kind)
        fixed (byte* solid = Solid)
        fixed (float* head = _head)
        fixed (byte* rest = _rest)
        fixed (float* q = lat == 1 ? _qx : _qy)
        fixed (float* outScale = _cellA)
        fixed (float* inScale = _cellB)
        fixed (float* vel = _vel)
        fixed (float* density = Density)
        fixed (float* fluidity = Fluidity)
        {
            ComputeHeads(water, kind, solid, head, density);
            ComputeRest(water, kind, solid, rest);
            new Span<float>(outScale, Length).Clear();
            new Span<float>(inScale, Length).Clear();

            for (int i = first; i <= last; i++)
            {
                int j = i + lat;
                if (solid[i] != 0 || solid[j] != 0)
                {
                    q[i] = 0f;
                    continue;
                }
                int wi = water[i], wj = water[j];
                if ((wi | wj) == 0 || (wi != 0 && wj != 0 && kind[i] != kind[j]))
                {
                    q[i] = 0f;
                    continue;
                }
                float flow = q[i] * SpreadMomentum + gain * fluidity[wi != 0 ? kind[i] : kind[j]] * (head[i] - head[j]);
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
            for (int i = first; i <= last + lat; i++)
            {
                float o = outScale[i];
                outScale[i] = o > water[i] ? water[i] / o : 1f;
            }
            for (int i = first; i <= last; i++)
            {
                float flow = q[i];
                if (flow > 0f) { flow *= outScale[i]; inScale[i + lat] += flow; }
                else if (flow < 0f) { flow *= outScale[i + lat]; inScale[i] -= flow; }
                q[i] = flow;
            }
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
                int src = flow > 0f ? i : j, dst = flow > 0f ? j : i;
                int t = (int)(Math.Abs(flow) * inScale[dst]);
                if (t > 0)
                {
                    int before = water[src];
                    float v = (flow > 0f ? 1f : -1f) * Math.Min(MaxJet, JetGain * MathF.Sqrt(Math.Abs(head[i] - head[j]) * strength));
                    int wd = water[dst];
                    vel[dst] = (vel[dst] * wd + v * t) / (wd + t);
                    water[dst] = wd + t;
                    kind[dst] = kind[src];
                    water[src] = before - t;
                    if (water[src] == 0) { kind[src] = 0; vel[src] = 0f; }
                }
                q[i] = flow > 0f ? t : -t;
            }
        }
    }

    /// <summary>Falling liquid keeps its sideways speed: it moves that share of itself sideways each tick,
    /// slowed by air. Resting liquid loses its speed to the floor.</summary>
    void Coast()
    {
        int lat = Gravity is GridDir.Up or GridDir.Down ? 1 : Stride;
        int first = Stride + 1, last = Length - Stride - 2;
        fixed (int* water = Water)
        fixed (byte* kind = Kind)
        fixed (byte* solid = Solid)
        fixed (byte* rest = _rest)
        fixed (float* vel = _vel)
        {
            // Moving forwards: walk against the motion so each cell moves once per tick.
            for (int i = last; i >= first; i--) CoastCell(water, kind, solid, rest, vel, i, lat, 1f);
            for (int i = first; i <= last; i++) CoastCell(water, kind, solid, rest, vel, i, -lat, -1f);
        }
    }

    static void CoastCell(int* water, byte* kind, byte* solid, byte* rest, float* vel, int i, int step, float sign)
    {
        int wi = water[i];
        float v = vel[i];
        if (wi == 0 || v * sign <= 0f) return;
        int j = i + step;
        if (rest[i] != 0 && (kind[j] != 0 || solid[j] != 0))
        {
            // Held up and pushing into liquid or a wall: the floor takes the speed.
            vel[i] = v * 0.8f;
            return;
        }
        vel[i] = v * AirDrag;
        if (solid[j] != 0 || (kind[j] != 0 && kind[j] != kind[i]))
        {
            vel[i] = 0f;
            return;
        }
        int room = Full - water[j];
        int t = Math.Min(room, (int)(wi * Math.Min(1f, v * sign)));
        if (t <= 0) return;
        int wj = water[j];
        vel[j] = (vel[j] * wj + vel[i] * t) / (wj + t);
        water[j] = wj + t;
        kind[j] = kind[i];
        water[i] = wi - t;
        if (water[i] == 0) { kind[i] = 0; vel[i] = 0f; }
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
        int lat = Gravity is GridDir.Up or GridDir.Down ? 1 : Stride;
        int visited = NextMark();
        int[] water = Water;
        byte[] kind = Kind, solid = Solid;
        for (int start = stride; start < Length - stride; start++)
        {
            if (water[start] < Brim || _mark[start] == visited) continue;
            byte k = kind[start];
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
                bool topOfColumn = solid[up] != 0 || wu < Brim || kind[up] != k;
                if (topOfColumn)
                {
                    float lvl = Elevation(c) + (float)water[c] / Full;
                    _srcCell[nSrc] = c;
                    _srcAvail[nSrc] = water[c];
                    _srcLevel[nSrc++] = lvl;
                    if (lvl > maxSrc) maxSrc = lvl;
                    if (solid[up] == 0 && wu > 0 && wu < Brim && kind[up] == k)
                    {
                        lvl = Elevation(up) + (float)wu / Full;
                        _srcCell[nSrc] = up;
                        _srcAvail[nSrc] = wu;
                        _srcLevel[nSrc++] = lvl;
                        if (lvl > maxSrc) maxSrc = lvl;
                    }
                }
                Visit(c, c + 1, k, visited, sinkMark, lat, ref sp, ref nSnk, ref minSnk);
                Visit(c, c - 1, k, visited, sinkMark, lat, ref sp, ref nSnk, ref minSnk);
                Visit(c, c + stride, k, visited, sinkMark, lat, ref sp, ref nSnk, ref minSnk);
                Visit(c, c - stride, k, visited, sinkMark, lat, ref sp, ref nSnk, ref minSnk);
            }
            if (!held || nSrc == 0 || nSnk == 0 || maxSrc - minSnk <= PressureDeadBand) continue;
            Level(k, nSrc, nSnk, minSnk, maxSrc, rate, strength);
        }
    }

    void Visit(int c, int n, byte k, int visited, int sinkMark, int lat, ref int sp, ref int nSnk, ref float minSnk)
    {
        if (Solid[n] != 0) return;
        int wn = Water[n];
        byte kn = Kind[n];
        if (kn != 0 && kn != k) return;
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
        _snkSide[nSnk] = n - c == lat ? 1 : n - c == -lat ? -1 : 0;
        _snkLevel[nSnk++] = lvl;
        if (lvl < minSnk) minSnk = lvl;
    }

    void Level(byte kind, int nSrc, int nSnk, float lo, float hi, float rate, float strength)
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
            Place(k, t, kind, level, strength);
            placed += t;
        }
        for (int k = 0; k < nSnk && placed < given; k++)
        {
            int t = (int)Math.Min(_snkRoom[k], given - placed);
            if (t <= 0) continue;
            Place(k, t, kind, level, strength);
            placed += t;
        }
        long remaining = placed;
        for (int k = 0; k < nSrc && remaining > 0; k++)
        {
            long g = Math.Min(_give[k], remaining);
            int c = _srcCell[k];
            Water[c] -= (int)g;
            if (Water[c] == 0) Empty(c);
            remaining -= g;
        }
    }

    /// <summary>Liquid pushed out of a side opening leaves with the speed its head gives it.</summary>
    void Place(int sink, int t, byte kind, float level, float strength)
    {
        if (t <= 0) return;
        int c = _snkCell[sink];
        _snkRoom[sink] -= t;
        int w = Water[c];
        float v = 0f;
        if (_snkSide[sink] != 0)
            v = _snkSide[sink] * Math.Min(MaxJet, JetGain * MathF.Sqrt(Math.Max(0f, level - _snkLevel[sink]) * strength));
        _vel[c] = (_vel[c] * w + v * t) / (w + t);
        Water[c] = w + t;
        Kind[c] = kind;
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

    void Exchange()
    {
        Array.Copy(Water, _prev, Length);
        int stride = Stride, first = stride, last = Length - stride - 1;
        fixed (int* water = Water)
        fixed (int* prev = _prev)
        fixed (byte* kind = Kind)
        fixed (byte* solid = Solid)
        {
            for (int i = first; i <= last; i++)
            {
                if (solid[i] != 0) continue;
                ExchangePair(water, prev, kind, solid, i, i + 1);
                ExchangePair(water, prev, kind, solid, i, i + stride);
            }
        }
    }

    static void ExchangePair(int* water, int* prev, byte* kind, byte* solid, int i, int j)
    {
        if (solid[j] != 0) return;
        int t = (int)((prev[i] - prev[j]) * ExchangeShare);
        if (t == 0) return;
        int src = t > 0 ? i : j, dst = t > 0 ? j : i;
        if (t < 0) t = -t;
        if (kind[src] == 0 || (kind[dst] != 0 && kind[dst] != kind[src]) || water[src] < t) return;
        water[src] -= t;
        water[dst] += t;
        kind[dst] = kind[src];
        if (water[src] == 0) kind[src] = 0;
    }

    // ---------------------------------------------------------------- evaporation

    static uint Hash(int i, long tick)
    {
        uint h = (uint)i * 2654435761u ^ (uint)tick * 2246822519u;
        h ^= h >> 15;
        h *= 2246822519u;
        return h ^ (h >> 13);
    }

    /// <summary>Exposed liquid evaporates into the empty cell above it (any empty neighbour with no
    /// gravity), more slowly as that air nears saturation.</summary>
    void Evaporate(float strength)
    {
        int up = strength > 0f ? -DownOffset() : 0, stride = Stride;
        for (int i = stride; i < Length - stride; i++)
        {
            int w = Water[i];
            if (w == 0) continue;
            int air;
            if (up != 0) air = Solid[i + up] == 0 && Kind[i + up] == 0 ? i + up : -1;
            else air = Kind[i + 1] == 0 && Solid[i + 1] == 0 ? i + 1 : Kind[i - 1] == 0 && Solid[i - 1] == 0 ? i - 1
                : Kind[i + stride] == 0 && Solid[i + stride] == 0 ? i + stride : Kind[i - stride] == 0 && Solid[i - stride] == 0 ? i - stride : -1;
            if (air < 0) continue;
            int k = Kind[i];
            int[] vapor = Vapor[k];
            int sat = Saturation[k];
            if (vapor[air] >= sat) continue;
            float amount = EvaporationRate[k] * (1f - (float)vapor[air] / sat);
            int units = (int)amount;
            if ((Hash(i, Ticks) & 0xFFFF) < (uint)((amount - units) * 65536f)) units++;
            units = Math.Min(units, Math.Min(w, sat - vapor[air]));
            if (units <= 0) continue;
            Water[i] = w - units;
            vapor[air] += units;
            _vaporPresent[k] = true;
            if (Water[i] == 0) Empty(i);
        }
    }

    /// <summary>Vapour spreads between empty cells. Vapour caught in a cell liquid has moved into
    /// condenses into it if it is the same liquid, or moves on to an empty neighbour.</summary>
    void SpreadVapor()
    {
        int stride = Stride, first = stride, last = Length - stride - 1;
        for (int k = 1; k <= KindCount; k++)
        {
            if (!_vaporPresent[k]) continue;
            int[] vapor = Vapor[k];
            Array.Copy(vapor, _prev, Length);
            long sum = 0;
            fixed (int* v = vapor)
            fixed (int* prev = _prev)
            fixed (byte* kind = Kind)
            fixed (byte* solid = Solid)
            fixed (int* water = Water)
            {
                for (int i = first; i <= last; i++)
                {
                    if (solid[i] != 0) continue;
                    if (kind[i] != 0)
                    {
                        int held = v[i];
                        if (held == 0) continue;
                        if (kind[i] == k)
                        {
                            int t = Math.Min(held, Full - water[i]);
                            water[i] += t;
                            v[i] -= t;
                            held -= t;
                        }
                        if (held == 0) continue;
                        int to = kind[i + 1] == 0 && solid[i + 1] == 0 ? i + 1 : kind[i - 1] == 0 && solid[i - 1] == 0 ? i - 1
                            : kind[i + stride] == 0 && solid[i + stride] == 0 ? i + stride : kind[i - stride] == 0 && solid[i - stride] == 0 ? i - stride : -1;
                        if (to >= 0)
                        {
                            v[to] += held;
                            v[i] -= held;
                        }
                        continue;
                    }
                    if (prev[i] == 0 && prev[i + 1] == 0 && prev[i + stride] == 0) continue;
                    sum += prev[i];
                    VaporPair(v, prev, kind, solid, i, i + 1);
                    VaporPair(v, prev, kind, solid, i, i + stride);
                }
            }
            if (sum == 0 && VaporOf(k) == 0) _vaporPresent[k] = false;
        }
    }

    static void VaporPair(int* v, int* prev, byte* kind, byte* solid, int i, int j)
    {
        if (solid[j] != 0 || kind[j] != 0) return;
        int t = (int)((prev[i] - prev[j]) * ExchangeShare);
        if (t == 0) return;
        if (t > 0 ? v[i] < t : v[j] < -t) return;
        v[i] -= t;
        v[j] += t;
    }
}
