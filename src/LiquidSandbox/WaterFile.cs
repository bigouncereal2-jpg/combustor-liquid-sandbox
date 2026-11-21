using Il2CppGasSim;
using LiquidSandbox.Simulation;

namespace LiquidSandbox;

/// <summary>Liquids saved beside an engine file as "name.engine.water". The game's own save keeps the
/// walls; this keeps every liquid, its vapour, the gravity direction and strength.</summary>
internal static class WaterFile
{
    const uint Magic = 0x57514C43; // "CLQW"
    const int FormatVersion = 2;

    public static string PathFor(string directory, string name) =>
        Path.Combine(directory, GasSimulation.SanitizeName(name) + GasSimulation.SaveExtension + ".water");

    public static void Save(string path, LiquidGrid grid)
    {
        string temp = path + ".tmp";
        using (var file = new BinaryWriter(File.Create(temp)))
        {
            file.Write(Magic);
            file.Write(FormatVersion);
            file.Write(grid.Width);
            file.Write(grid.Height);
            file.Write((int)grid.Gravity);
            file.Write(grid.Strength);
            file.Write(grid.Evaporation);
            WriteRuns(file, grid, (x, y) => grid.Get(x, y) | (grid.KindAt(x, y) << 20));
            for (int k = 1; k <= LiquidGrid.KindCount; k++)
            {
                int kind = k;
                WriteRuns(file, grid, (x, y) => grid.VaporAt(x, y, kind));
            }
        }
        File.Move(temp, path, true);
    }

    // Run-length pairs over the cells in row order: count, value.
    static void WriteRuns(BinaryWriter file, LiquidGrid grid, Func<int, int, int> value)
    {
        int run = 0, last = 0;
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                int v = value(x, y);
                if (run > 0 && v == last) { run++; continue; }
                if (run > 0) { file.Write(run); file.Write(last); }
                last = v;
                run = 1;
            }
        if (run > 0) { file.Write(run); file.Write(last); }
    }

    static void ReadRuns(BinaryReader file, LiquidGrid grid, Action<int, int, int> apply)
    {
        long cells = (long)grid.Width * grid.Height, at = 0;
        while (at < cells)
        {
            int run = file.ReadInt32(), value = file.ReadInt32();
            if (run <= 0 || at + run > cells) throw new InvalidDataException("damaged water data");
            for (int k = 0; k < run; k++, at++)
                if (value != 0) apply((int)(at % grid.Width), (int)(at / grid.Width), value);
        }
    }

    /// <summary>Loads liquids saved for the same grid size. Returns false when there is no file.</summary>
    public static bool Load(string path, LiquidGrid grid)
    {
        if (!File.Exists(path)) return false;
        using var file = new BinaryReader(File.OpenRead(path));
        uint magic = file.ReadUInt32();
        int version = file.ReadInt32();
        if (magic != Magic || version < 1 || version > FormatVersion) throw new InvalidDataException("not a Liquid Sandbox water file");
        int width = file.ReadInt32(), height = file.ReadInt32();
        if (width != grid.Width || height != grid.Height) throw new InvalidDataException($"saved for a {width} x {height} grid");
        grid.Clear();
        grid.Gravity = (GridDir)Math.Clamp(file.ReadInt32(), 0, 3);
        grid.Strength = file.ReadSingle();
        if (version == 1)
        {
            file.ReadInt64();
            ReadRuns(file, grid, (x, y, v) => grid.Add(x, y, Math.Min(v, LiquidGrid.Full), LiquidGrid.Water1));
            return true;
        }
        grid.Evaporation = file.ReadBoolean();
        ReadRuns(file, grid, (x, y, v) => grid.Add(x, y, v & 0xFFFFF, Math.Clamp(v >> 20, 1, LiquidGrid.KindCount)));
        for (int k = 1; k <= LiquidGrid.KindCount; k++)
        {
            int kind = k;
            ReadRuns(file, grid, (x, y, v) =>
            {
                if (!grid.IsSolid(x, y) && grid.KindAt(x, y) == 0) grid.Vapor[kind][grid.Index(x, y)] += Math.Max(0, v);
            });
        }
        return true;
    }
}
