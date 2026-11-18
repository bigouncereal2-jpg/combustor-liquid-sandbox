using System.Diagnostics;
using System.IO.Compression;
using LiquidSandbox.Simulation;

// Scenario checks for the liquid solver. Run: dotnet run -c Release [-- --png <dir>]
// Exit code is the number of failed checks.

int failures = 0;
string pngDir = null;
for (int a = 0; a < args.Length; a++)
    if (args[a] == "--png" && a + 1 < args.Length) pngDir = args[++a];
if (pngDir != null) Directory.CreateDirectory(pngDir);

void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    if (!ok) failures++;
}

void Run(string name, Action body)
{
    Console.WriteLine(name);
    try { body(); }
    catch (Exception e) { Check(false, "threw " + e); }
}

const int F = LiquidGrid.Full;

byte[] Box(int w, int h) => new byte[w * h];

void Wall(byte[] s, int w, int x0, int y0, int x1, int y1)
{
    for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            s[y * w + x] = 1;
}

void Fill(LiquidGrid g, int x0, int y0, int x1, int y1, int units = F)
{
    for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            g.Add(x, y, units);
}

bool CellsValid(LiquidGrid g)
{
    for (int i = 0; i < g.Length; i++)
    {
        int w = g.Water[i];
        if (w < 0 || w > F) return false;
        if (g.Solid[i] != 0 && w != 0) return false;
    }
    return true;
}

// Water held by column x, in cells.
double Column(LiquidGrid g, int x, int y0 = 0, int y1 = -1)
{
    if (y1 < 0) y1 = g.Height - 1;
    long sum = 0;
    for (int y = y0; y <= y1; y++) sum += g.Get(x, y);
    return (double)sum / F;
}

double Row(LiquidGrid g, int y, int x0 = 0, int x1 = -1)
{
    if (x1 < 0) x1 = g.Width - 1;
    long sum = 0;
    for (int x = x0; x <= x1; x++) sum += g.Get(x, y);
    return (double)sum / F;
}

// Surface level of a column for downward gravity: open cells below the topmost water, in cells.
double Level(LiquidGrid g, int x, int y0, int y1)
{
    double level = y0;
    for (int y = y0; y <= y1; y++)
    {
        int w = g.Get(x, y);
        if (w > 0) level = y + (double)w / F;
    }
    return level;
}

void Png(LiquidGrid g, string name, int scale = 4)
{
    if (pngDir == null) return;
    int w = g.Width * scale, h = g.Height * scale;
    var rgb = new byte[h * (w * 3 + 1)];
    for (int py = 0; py < h; py++)
    {
        int row = py * (w * 3 + 1);
        rgb[row] = 0;
        int gy = g.Height - 1 - py / scale;
        float sub = 1f - ((py % scale) + 0.5f) / scale; // 0 at bottom of cell, 1 at top
        for (int px = 0; px < w; px++)
        {
            int gx = px / scale;
            float subx = ((px % scale) + 0.5f) / scale;
            byte r = 8, gg = 8, b = 13;
            if (g.IsSolid(gx, gy)) { r = 90; gg = 94; b = 104; }
            else
            {
                float f = (float)g.Get(gx, gy) / F;
                float along = g.Gravity switch
                {
                    GridDir.Down => sub,
                    GridDir.Up => 1f - sub,
                    GridDir.Left => subx,
                    _ => 1f - subx,
                };
                bool wet = g.Strength > 0 ? along < f : f > 0;
                if (f >= 1f || wet)
                {
                    float a = g.Strength > 0 || f >= 1f ? 1f : f;
                    r = (byte)(8 + (40 - 8) * a); gg = (byte)(8 + (120 - 8) * a); b = (byte)(13 + (220 - 13) * a);
                }
            }
            int o = row + 1 + px * 3;
            rgb[o] = r; rgb[o + 1] = gg; rgb[o + 2] = b;
        }
    }
    File.WriteAllBytes(Path.Combine(pngDir, name + ".png"), EncodePng(w, h, rgb));
}

static byte[] EncodePng(int w, int h, byte[] raw)
{
    using var ms = new MemoryStream();
    ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    void Chunk(string type, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length); Array.Reverse(len);
        ms.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        ms.Write(t);
        ms.Write(data);
        uint crc = Crc(t, data);
        var c = BitConverter.GetBytes(crc); Array.Reverse(c);
        ms.Write(c);
    }
    var ihdr = new byte[13];
    void Be(int v, int at) { ihdr[at] = (byte)(v >> 24); ihdr[at + 1] = (byte)(v >> 16); ihdr[at + 2] = (byte)(v >> 8); ihdr[at + 3] = (byte)v; }
    Be(w, 0); Be(h, 4); ihdr[8] = 8; ihdr[9] = 2;
    Chunk("IHDR", ihdr);
    using (var z = new MemoryStream())
    {
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
        Chunk("IDAT", z.ToArray());
    }
    Chunk("IEND", Array.Empty<byte>());
    return ms.ToArray();
}

static uint Crc(byte[] a, byte[] b)
{
    uint c = 0xFFFFFFFF;
    foreach (var arr in new[] { a, b })
        foreach (byte x in arr)
        {
            c ^= x;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
    return ~c;
}

// ------------------------------------------------------------------ scenarios

Run("Random walls, painting, gravity changes and wall edits never create or lose water", () =>
{
    var rng = new Random(1234);
    int w = 96, h = 64;
    var g = new LiquidGrid(w, h);
    var solid = Box(w, h);
    for (int k = 0; k < w * h / 10; k++) solid[rng.Next(w * h)] = 1;
    g.SyncSolid(solid);
    long expected = 0;
    for (int k = 0; k < 2500; k++) expected += g.Add(rng.Next(w), rng.Next(h), rng.Next(1, F));
    Check(g.Total() == expected, $"initial total {g.Total()} == {expected}");
    bool totalsOk = true, cellsOk = true;
    for (int t = 0; t < 6000; t++)
    {
        if (t % 150 == 0)
        {
            g.Gravity = (GridDir)rng.Next(4);
            g.Strength = rng.Next(5) == 0 ? 0f : (float)(rng.NextDouble() * LiquidGrid.MaxStrength);
        }
        if (t % 37 == 0)
        {
            for (int k = 0; k < 40; k++) solid[rng.Next(w * h)] ^= 1;
            g.SyncSolid(solid);
        }
        if (t % 23 == 0)
        {
            expected += g.Add(rng.Next(w), rng.Next(h), rng.Next(F));
            expected -= g.Remove(rng.Next(w), rng.Next(h), rng.Next(F));
        }
        g.Step();
        if (g.Total() != expected) totalsOk = false;
        if (t % 10 == 0 && !CellsValid(g)) cellsOk = false;
    }
    Check(totalsOk, $"total stays exact every tick (final {g.Total()} == {expected})");
    Check(cellsOk && CellsValid(g), "every cell stays within 0..Full and walls hold no water");
});

Run("A block of water falls, spreads and settles flat in a box", () =>
{
    int w = 60, h = 40;
    var g = new LiquidGrid(w, h);
    Fill(g, 20, 28, 39, 37);
    long total = g.Total();
    Png(g, "settle_000");
    for (int t = 1; t <= 1200; t++)
    {
        g.Step();
        if (pngDir != null && (t == 20 || t == 60 || t == 200)) Png(g, $"settle_{t:000}");
    }
    Png(g, "settle_end");
    Check(g.Total() == total, "total unchanged");
    double cells = (double)total / F;
    double expectRows = cells / w;
    double lo = double.MaxValue, hi = double.MinValue;
    for (int x = 0; x < w; x++)
    {
        double lv = Level(g, x, 0, h - 1);
        lo = Math.Min(lo, lv); hi = Math.Max(hi, lv);
    }
    Check(hi - lo < 0.35, $"surface flat: levels {lo:0.000}..{hi:0.000} (expected about {expectRows:0.000})");
    Check(Row(g, 0) > w - 0.01 && Row(g, 1) > w - 0.01, "bottom rows full");
    Check(Row(g, (int)expectRows + 1) < 0.01, "nothing left above the surface");
});

Run("Water in a cup stays in the cup and its surface levels out", () =>
{
    int w = 50, h = 40;
    var g = new LiquidGrid(w, h);
    var s = Box(w, h);
    Wall(s, w, 10, 5, 30, 5);   // floor
    Wall(s, w, 10, 5, 10, 25);  // left side
    Wall(s, w, 30, 5, 30, 25);  // right side
    g.SyncSolid(s);
    Fill(g, 11, 20, 20, 35);    // pour into the left half, partly above the cup
    long total = g.Total();
    for (int t = 0; t < 1500; t++) g.Step();
    Png(g, "cup_end");
    Check(g.Total() == total, "total unchanged");
    double inside = 0;
    for (int x = 11; x <= 29; x++) inside += Column(g, x, 6, 25);
    Check(Math.Abs(inside - (double)total / F) < 0.01, $"all water inside the cup ({inside:0.00} of {(double)total / F:0.00})");
    double lo = double.MaxValue, hi = double.MinValue;
    for (int x = 11; x <= 29; x++) { double lv = Level(g, x, 6, 25); lo = Math.Min(lo, lv); hi = Math.Max(hi, lv); }
    Check(hi - lo < 0.35, $"level within the cup: {lo:0.000}..{hi:0.000}");
});

Run("Connected vessels (U tube) reach one level", () =>
{
    int w = 40, h = 50;
    var g = new LiquidGrid(w, h);
    var s = Box(w, h);
    Wall(s, w, 0, 0, w - 1, h - 1);
    // carve: left leg x 5..8, right leg x 25..28, bottom channel y 2..4 from x 5..28
    for (int y = 2; y <= 47; y++) for (int x = 5; x <= 8; x++) s[y * w + x] = 0;
    for (int y = 2; y <= 47; y++) for (int x = 25; x <= 28; x++) s[y * w + x] = 0;
    for (int y = 2; y <= 4; y++) for (int x = 5; x <= 28; x++) s[y * w + x] = 0;
    g.SyncSolid(s);
    for (int y = 2; y <= 4; y++) for (int x = 5; x <= 28; x++) g.Add(x, y, F);
    for (int y = 5; y <= 40; y++) for (int x = 5; x <= 8; x++) g.Add(x, y, F);
    long total = g.Total();
    Png(g, "utube_000");
    int settledAt = -1;
    double left = 0, right = 0;
    for (int t = 1; t <= 4000; t++)
    {
        g.Step();
        left = Level(g, 6, 2, 47);
        right = Level(g, 26, 2, 47);
        if (settledAt < 0 && Math.Abs(left - right) < 0.5) settledAt = t;
        if (pngDir != null && (t == 30 || t == 120)) Png(g, $"utube_{t:000}");
    }
    Png(g, "utube_end");
    Check(g.Total() == total, "total unchanged");
    Check(Math.Abs(left - right) < 0.5, $"levels match: left {left:0.00}, right {right:0.00} (within 0.5 after {settledAt} ticks)");
    Check(settledAt > 0 && settledAt < 64 * 20, $"levels meet within 20 s of game time ({settledAt} ticks)");
});

Run("Turning gravity moves settled water to the new floor without a reset", () =>
{
    int w = 40, h = 30;
    var g = new LiquidGrid(w, h);
    Fill(g, 0, 0, w - 1, 7);
    long total = g.Total();
    g.Gravity = GridDir.Left;
    for (int t = 0; t < 1500; t++) g.Step();
    Png(g, "rotate_left");
    Check(g.Total() == total, "total unchanged");
    double cells = (double)total / F;
    double expectCols = cells / h;
    Check(Column(g, 0) > h - 0.01 && Column(g, 1) > h - 0.01, "left columns full");
    double lo = double.MaxValue, hi = double.MinValue;
    for (int y = 0; y < h; y++)
    {
        double lv = 0;
        for (int x = 0; x < w; x++) { int c = g.Get(x, y); if (c > 0) lv = x + (double)c / F; }
        lo = Math.Min(lo, lv); hi = Math.Max(hi, lv);
    }
    Check(hi - lo < 0.35, $"surface perpendicular to gravity: {lo:0.000}..{hi:0.000} (expected about {expectCols:0.00})");
    g.Gravity = GridDir.Up;
    for (int t = 0; t < 1500; t++) g.Step();
    Png(g, "rotate_up");
    Check(Row(g, h - 1) > w - 0.01, "after turning up, the top row is full");
    Check(g.Total() == total, "total unchanged after second turn");
});

Run("Zero gravity has no preferred direction and evens out neighbours", () =>
{
    int w = 40, h = 30;
    var g = new LiquidGrid(w, h) { Strength = 0f };
    Fill(g, 15, 10, 24, 19);
    long total = g.Total();
    double meanBefore = (double)total / (w * h);
    for (int t = 0; t < 4000; t++) g.Step();
    Png(g, "zerog_end");
    Check(g.Total() == total, "total unchanged");
    int lo = int.MaxValue, hi = int.MinValue;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int c = g.Get(x, y); lo = Math.Min(lo, c); hi = Math.Max(hi, c); }
    Check((hi - lo) < meanBefore * 0.05, $"cells even: {lo / (double)F:0.0000}..{hi / (double)F:0.0000} (mean {meanBefore / F:0.0000})");
    // symmetric blob stays centred: compare mass in the left and right halves, top and bottom halves
    var g2 = new LiquidGrid(40, 30) { Strength = 0f };
    Fill(g2, 18, 13, 21, 16);
    for (int t = 0; t < 300; t++) g2.Step();
    double l = 0, r = 0, b = 0, tp = 0;
    for (int x = 0; x < 20; x++) l += Column(g2, x);
    for (int x = 20; x < 40; x++) r += Column(g2, x);
    for (int y = 0; y < 15; y++) b += Row(g2, y);
    for (int y = 15; y < 30; y++) tp += Row(g2, y);
    Check(Math.Abs(l - r) < 0.05 && Math.Abs(b - tp) < 0.05, $"no drift: left {l:0.000} right {r:0.000} bottom {b:0.000} top {tp:0.000}");
});

Run("Gravity strength changes speed, not the final state", () =>
{
    int w = 30, h = 30;
    int Ticks(float strength)
    {
        var g = new LiquidGrid(w, h) { Strength = strength };
        Fill(g, 10, 20, 19, 29);
        for (int t = 1; t <= 5000; t++)
        {
            g.Step();
            if (Row(g, 0) > w - 0.01 && Row(g, 4) < 0.01 && Level(g, 0, 0, h - 1) > 3.0) return t;
        }
        return -1;
    }
    int slow = Ticks(0.25f), normal = Ticks(1f), fast = Ticks(2f);
    Check(slow > normal && normal > fast && fast > 0, $"settle ticks: 0.25 g {slow}, 1 g {normal}, 2 g {fast}");
});

Run("Painting walls over a sealed, full container keeps every drop", () =>
{
    int w = 30, h = 20;
    var g = new LiquidGrid(w, h);
    var s = Box(w, h);
    Wall(s, w, 5, 5, 20, 5); Wall(s, w, 5, 15, 20, 15); Wall(s, w, 5, 5, 5, 15); Wall(s, w, 20, 5, 20, 15);
    g.SyncSolid(s);
    Fill(g, 6, 6, 19, 14);
    long total = g.Total();
    Wall(s, w, 8, 8, 12, 12);   // a solid block inside the full tank
    g.SyncSolid(s);
    Check(g.Total() == total, $"total kept after displacement ({g.Total()} == {total}, overflow {g.Overflow})");
    Check(CellsValid(g), "cells valid");
    for (int t = 0; t < 200; t++) g.Step();
    Check(g.Total() == total, "total kept while the overflow waits for room");
    s[5 * w + 10] = 0;  // open a hole in the floor
    g.SyncSolid(s);
    for (int t = 0; t < 2000; t++) g.Step();
    Check(g.Total() == total, "total kept after draining through a hole");
    Check(g.Overflow == 0, "overflow placed once there was room");
});

Run("Changing grid size keeps the water", () =>
{
    var g = new LiquidGrid(40, 30);
    Fill(g, 5, 0, 34, 10);
    long total = g.Total();
    var bigger = g.Resized(60, 40, 10, 5, Box(60, 40));
    Check(bigger.Total() == total, "bigger grid total");
    Check(bigger.Get(15, 5) == F, "water moved by the offset");
    var smaller = bigger.Resized(20, 12, -20, -5, Box(20, 12));
    Check(smaller.Total() == total || smaller.Overflow > 0, $"smaller grid total {smaller.Total()} == {total} (overflow {smaller.Overflow})");
});

Run("Dam break frames", () =>
{
    int w = 120, h = 50;
    var g = new LiquidGrid(w, h);
    Fill(g, 0, 0, 24, 39);
    long total = g.Total();
    Png(g, "dam_000", 3);
    for (int t = 1; t <= 600; t++)
    {
        g.Step();
        if (t is 8 or 16 or 32 or 64 or 128 or 600) Png(g, $"dam_{t:000}", 3);
    }
    Check(g.Total() == total, "total unchanged");
    double lo = double.MaxValue, hi = double.MinValue;
    for (int x = 0; x < w; x++) { double lv = Level(g, x, 0, h - 1); lo = Math.Min(lo, lv); hi = Math.Max(hi, lv); }
    Check(hi - lo < 0.35, $"settled flat after 600 ticks: {lo:0.000}..{hi:0.000}");
});

Run("Performance on the default 256 x 160 sandbox grid", () =>
{
    int w = 256, h = 160;
    var g = new LiquidGrid(w, h);
    var s = Box(w, h);
    var rng = new Random(7);
    for (int k = 0; k < 40; k++)
    {
        int x0 = rng.Next(w - 30), y0 = rng.Next(h - 10);
        Wall(s, w, x0, y0, x0 + rng.Next(3, 30), y0);
    }
    g.SyncSolid(s);
    Fill(g, 0, 0, w - 1, 60);
    Fill(g, 40, 100, 200, 140);
    for (int t = 0; t < 200; t++) g.Step();
    var sw = Stopwatch.StartNew();
    int n = 640;
    for (int t = 0; t < n; t++)
    {
        if (t % 128 == 0) g.Gravity = (GridDir)((t / 128) % 4);
        g.Step();
    }
    double ms = sw.Elapsed.TotalMilliseconds / n;
    Console.WriteLine($"       {ms:0.000} ms per tick, {ms * 64:0.0} ms per second of game time at 64 ticks/s");
    Check(ms < 2.0, "under 2 ms per tick");
    g.Strength = 0f;
    sw.Restart();
    for (int t = 0; t < 256; t++) g.Step();
    double zg = sw.Elapsed.TotalMilliseconds / 256;
    Console.WriteLine($"       zero gravity: {zg:0.000} ms per tick");
    Check(zg < 2.0, "zero gravity under 2 ms per tick");
    long before = GC.GetAllocatedBytesForCurrentThread();
    g.Strength = 1f;
    for (int t = 0; t < 256; t++) g.Step();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated == 0, $"no allocations while stepping ({allocated} bytes)");
});

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures;
