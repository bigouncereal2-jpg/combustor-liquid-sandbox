using Il2CppGasSim;
using LiquidSandbox.Simulation;

namespace LiquidSandbox;

/// <summary>Water saved beside an engine file as "name.engine.water". The game's own save keeps the
/// walls; this keeps the water, the gravity direction and the strength.</summary>
internal static class WaterFile
{
    const uint Magic = 0x57514C43; // "CLQW"
    const int FormatVersion = 1;

    public static string PathFor(string directory, string name) =>
        Path.Combine(directory, GasSimulation.SanitizeName(name) + GasSimulation.SaveExtension + ".water");

    public static void Save(string path, LiquidGrid water)
    {
        string temp = path + ".tmp";
        using (var file = new BinaryWriter(File.Create(temp)))
        {
            file.Write(Magic);
            file.Write(FormatVersion);
            file.Write(water.Width);
            file.Write(water.Height);
            file.Write((int)water.Gravity);
            file.Write(water.Strength);
            file.Write(water.Total());
            // Run-length pairs over the cells in row order: count, amount.
            int run = 0, last = -1;
            for (int y = 0; y < water.Height; y++)
            {
                for (int x = 0; x < water.Width; x++)
                {
                    int amount = water.Get(x, y);
                    if (amount == last && run < int.MaxValue)
                    {
                        run++;
                        continue;
                    }
                    if (run > 0)
                    {
                        file.Write(run);
                        file.Write(last);
                    }
                    last = amount;
                    run = 1;
                }
            }
            if (run > 0)
            {
                file.Write(run);
                file.Write(last);
            }
        }
        File.Move(temp, path, true);
    }

    /// <summary>Loads water saved for the same grid size. Returns false when there is no file.</summary>
    public static bool Load(string path, LiquidGrid water)
    {
        if (!File.Exists(path)) return false;
        using var file = new BinaryReader(File.OpenRead(path));
        if (file.ReadUInt32() != Magic || file.ReadInt32() != FormatVersion) throw new InvalidDataException("not a Liquid Sandbox water file");
        int width = file.ReadInt32(), height = file.ReadInt32();
        if (width != water.Width || height != water.Height) throw new InvalidDataException($"saved for a {width} x {height} grid");
        int gravity = file.ReadInt32();
        float strength = file.ReadSingle();
        long savedTotal = file.ReadInt64();
        water.Clear();
        water.Gravity = (GridDir)Math.Clamp(gravity, 0, 3);
        water.Strength = strength;
        long cells = (long)width * height, at = 0, placed = 0;
        while (at < cells)
        {
            int run = file.ReadInt32(), amount = file.ReadInt32();
            if (run <= 0 || amount < 0 || amount > LiquidGrid.Full || at + run > cells) throw new InvalidDataException("damaged water data");
            for (int k = 0; k < run; k++, at++)
                if (amount > 0) placed += water.Add((int)(at % width), (int)(at / width), amount);
        }
        if (placed != savedTotal)
            LiquidSandboxMod.Log.Msg($"Some saved water landed in walls changed since saving and was left out ({(savedTotal - placed) / (double)LiquidGrid.Full:0.##} cells).");
        return true;
    }
}
