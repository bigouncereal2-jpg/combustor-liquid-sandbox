using Il2CppGasSim;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LiquidSandbox.Simulation;
using UnityEngine;

namespace LiquidSandbox;

/// <summary>One Liquid Sandbox run: Combustor's sandbox scene with the gas solver replaced by water.</summary>
public sealed class LiquidSession
{
    public static LiquidSession Current { get; private set; }

    /// <summary>Water volume one mole of the gas brush pours, in cells. With the default brush
    /// (40 mol/s over radius 3) each cell under it fills in about a quarter of a second.</summary>
    const float CellsPerMole = 3.7f;
    /// <summary>Cells of water the vacuum brush drains per unit of its per-frame share.</summary>
    const float CellsPerDrain = 1f;

    public GasSimulation Sim { get; }
    public GasSimInput Input { get; }
    public GasSimRenderer Renderer { get; }
    public GasSimHud GameHud { get; }
    public LiquidGrid Water { get; private set; }
    internal LiquidHud Hud { get; }

    readonly IntPtr _sim, _input, _renderer, _hud;
    IntPtr _grid;
    int _pendingDx, _pendingDy;
    bool _pendingResize;
    readonly WaterView _view = new();
    readonly TextSwaps _texts = new();
    bool _failed;

    public GridDir Gravity
    {
        get => Water.Gravity;
        set => Water.Gravity = value;
    }

    /// <summary>Liquid the Liquid tool pours (LiquidGrid.Water1, Gasoline or Diesel).</summary>
    public int SelectedKind { get; set; } = LiquidGrid.Water1;

    public float Strength
    {
        get => Water.Strength;
        set => Water.Strength = value;
    }

    LiquidSession(GasSimulation sim)
    {
        Sim = sim;
        _sim = sim.Pointer;
        Input = sim.GetComponent<GasSimInput>();
        Renderer = sim.GetComponent<GasSimRenderer>();
        GameHud = sim.GetComponent<GasSimHud>();
        _input = Input != null ? Input.Pointer : IntPtr.Zero;
        _renderer = Renderer != null ? Renderer.Pointer : IntPtr.Zero;
        _hud = GameHud != null ? GameHud.Pointer : IntPtr.Zero;
        Hud = new LiquidHud(this);
        var grid = sim.Grid;
        _grid = grid.Pointer;
        Water = new LiquidGrid(grid.Width, grid.Height);
        Water.SyncSolid(grid.Solid.AsSpan());
    }

    internal static void Begin(GasSimulation sim)
    {
        End();
        var session = new LiquidSession(sim);
        Current = session;
        session.Configure();
        LiquidSandboxMod.Log.Msg($"Liquid Sandbox started on a {session.Water.Width} x {session.Water.Height} grid.");
    }

    internal static void End()
    {
        var s = Current;
        if (s == null) return;
        Current = null;
        s._texts.Restore();
        s._view.Dispose();
    }

    public bool Owns(GasGrid grid) => grid != null && grid.Pointer == _grid;
    public bool Owns(GasSimulation sim) => sim != null && sim.Pointer == _sim;
    public bool Owns(GasSimInput input) => input != null && input.Pointer == _input;
    public bool Owns(GasSimHud hud) => hud != null && hud.Pointer == _hud;

    /// <summary>Every sandbox tool is available. Injectors pour the picked liquid, holes drain it,
    /// heat and igniters boil it into vapour, and walls, valves and pistons move it aside.</summary>
    internal static bool ToolAllowed(Tool t) => true;

    void Configure()
    {
        Sim.ExampleHint = LiquidHud.HintText;
        Input?.SelectTool(Tool.Wall);
        _texts.Apply();
    }

    /// <summary>No gas at all, closed edges and no wind: the grid only holds walls and water.</summary>
    void SealAndEmpty()
    {
        var settings = Sim.settings;
        if (settings != null)
        {
            settings.vacuumAmbient = true;
            settings.openBoundaries = false;
            settings.windOn = false;
        }
        var grid = Sim.Grid;
        if (grid == null) return;
        var live = Sim.LiveSettings();
        if (live != null)
        {
            live.vacuumAmbient = true;
            live.openBoundaries = false;
            live.windOn = false;
            grid.Settings = live;
        }
        grid.FillAmbient();
    }

    internal void Update()
    {
        if (Sim == null || Sim.WasCollected)
        {
            End();
            return;
        }
    }

    /// <summary>Runs one fixed tick of water instead of the gas solver. Returns false for grids
    /// that are not this sandbox's, so the game steps those itself.</summary>
    internal bool TryTick(GasGrid grid)
    {
        if (grid == null) return false;
        if (grid.Pointer != _grid)
        {
            var current = Sim.Grid;
            if (current == null || current.Pointer != grid.Pointer) return false;
            Adopt(grid);
        }
        if (_failed) return true;
        try
        {
            Water.SyncSolid(grid.Solid.AsSpan());
            Water.Step();
            _gasGrid = grid;
            _temperature = grid.Temperature;
            if (_fuelSpecies == -1) ConnectFuelGas(grid);
            else if (_fuelSpecies >= 0 && Water.Vent == null) ConnectFuelGas(grid);
        }
        catch (Exception error)
        {
            Fail("simulation", error);
        }
        return true;
    }

    /// <summary>The sandbox swapped in a new grid (size change or a load at another size).</summary>
    void Adopt(GasGrid grid)
    {
        var solid = grid.Solid.AsSpan();
        long before = Water.Total();
        if (_pendingResize)
        {
            // Move the water by the same offset the game moved the walls by.
            int dx = _pendingDx, dy = _pendingDy;
            if (WallMatches(solid, grid.Width, grid.Height, -dx, -dy) > WallMatches(solid, grid.Width, grid.Height, dx, dy)) { dx = -dx; dy = -dy; }
            Water = Water.Resized(grid.Width, grid.Height, dx, dy, solid);
        }
        else
        {
            var next = new LiquidGrid(grid.Width, grid.Height) { Gravity = Water.Gravity, Strength = Water.Strength, Evaporation = Water.Evaporation };
            next.SyncSolid(solid);
            Water = next;
        }
        _pendingResize = false;
        _grid = grid.Pointer;
        _view.Reset();
        if (Water.Total() != before && before != 0)
            LiquidSandboxMod.Log.Msg($"Grid changed to {grid.Width} x {grid.Height}; water {before / (double)LiquidGrid.Full:0.##} -> {Water.Total() / (double)LiquidGrid.Full:0.##} cells.");
    }

    int WallMatches(ReadOnlySpan<byte> solid, int width, int height, int dx, int dy)
    {
        int matches = 0;
        for (int y = 0; y < Water.Height; y++)
            for (int x = 0; x < Water.Width; x++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)width && (uint)ny < (uint)height && Water.IsSolid(x, y) && solid[ny * width + nx] != 0) matches++;
            }
        return matches;
    }

    internal void GridReplaced(int dx, int dy)
    {
        _pendingDx = dx;
        _pendingDy = dy;
        _pendingResize = true;
        var grid = Sim.Grid;
        if (grid != null && grid.Pointer != _grid) Adopt(grid);
    }

    internal void Pour(int x, int y, float moles)
    {
        if (!(moles > 0f)) return;
        Water.Add(x, y, ToUnits(moles * CellsPerMole), SelectedKind);
    }

    internal void Drain(int x, int y, float share)
    {
        if (!(share > 0f)) return;
        Water.Remove(x, y, ToUnits(share * CellsPerDrain));
    }

    /// <summary>True when the cursor is over a cell holding liquid (the inspector then describes it).</summary>
    internal bool HoversLiquid => Input != null && Input.HoverValid && Water.Get(Input.HoverX, Input.HoverY) > 0;

    /// <summary>Heat boils the liquid under it into vapour (the heat brush and igniters).</summary>
    GasGrid _gasGrid;

    internal void Heat(int x, int y, float temperature)
    {
        _heatTarget = temperature;
        Water.Boil(x, y, LiquidGrid.Full / 16);
    }

    // Fuels evaporate into the game's own Fuel gas, so they burn exactly like fuel in an engine: the
    // game's igniters, reactions and flames. Hot air above a pool makes it evaporate faster, which keeps
    // a fire fed until the liquid is gone or the air runs out.
    static readonly float[] VentRate = { 0f, 0f, 26f, 0.6f };      // units per tick per exposed cell at 293 K
    static readonly float[] VentHeat = { 1f, 1f, 90f, 45f };       // kelvin per e-fold of that rate
    const float MaxVentBoost = 60f;
    const float MolesPerLiquidCell = 3f;
    int _fuelSpecies = -1;
    float _heatTarget = 600f;
    readonly float[] _ventCarry = new float[LiquidGrid.KindCount + 1];
    Il2CppStructArray<float> _temperature;

    void ConnectFuelGas(GasGrid grid)
    {
        var species = grid.Species;
        for (int k = 0; species != null && k < species.Length; k++)
            if (species[k] != null && string.Equals(species[k].name, "Fuel", StringComparison.OrdinalIgnoreCase)) { _fuelSpecies = k; break; }
        LiquidSandboxMod.Log.Msg(_fuelSpecies >= 0
            ? $"Gasoline and diesel evaporate into the game's {species[_fuelSpecies].name} gas (species {_fuelSpecies})."
            : "No Fuel gas found in this build; fuel vapour stays in the liquid layer.");
        if (_fuelSpecies < 0) { _fuelSpecies = -2; return; }
        Water.Vents[LiquidGrid.Gasoline] = true;
        Water.Vents[LiquidGrid.Diesel] = true;
        Water.Vent = VentToGas;
    }

    int VentToGas(int kind, int liquidCell, int airCell, int most, bool forced)
    {
        var grid = _gasGrid;
        if (grid == null) return 0;
        int stride = Water.Stride, x = airCell % stride - 1, y = airCell / stride - 1;
        var temps = _temperature;
        float t = temps != null ? temps[y * Water.Width + x] : 293f;
        float rate = forced ? most : VentRate[kind] * Math.Min(MaxVentBoost, MathF.Exp(Math.Max(0f, t - 293f) / VentHeat[kind]));
        int units = (int)rate;
        _ventCarry[kind] += rate - units;
        if (_ventCarry[kind] >= 1f) { units++; _ventCarry[kind] -= 1f; }
        units = Math.Min(units, most);
        if (units <= 0) return 0;
        grid.AddGas(x, y, _fuelSpecies, units / (float)LiquidGrid.Full * MolesPerLiquidCell, forced ? Math.Max(t, _heatTarget) : Math.Max(t, 293f));
        return units;
    }

    static int ToUnits(float cells) => (int)Math.Min(int.MaxValue, Math.Ceiling(cells * LiquidGrid.Full));

    public void ClearWater() => Water.Clear();

    internal void Render(Il2CppStructArray<Color32> pixels)
    {
        if (_failed) return;
        var grid = Sim.Grid;
        if (grid == null || grid.Pointer != _grid) return;
        _view.Draw(Renderer, Water, grid.Solid.AsSpan(), pixels.AsSpan());
    }

    internal void HandleKeys()
    {
        var input = Input;
        if (input == null || input.textEntry || input.searchEntry || input.scriptEntry || input.valveTextEntry) return;
        if (GameHud != null && GameHud.ModalOpen) return;
        if (input.tool is not (Tool.Wall or Tool.Gas or Tool.Vacuum)) return;
        if (SimInputAdapter.KeyPressed(KeyCode.F)) TurnClockwise();
    }

    /// <summary>The next direction clockwise on screen, as the wind's hint describes.</summary>
    public void TurnClockwise()
    {
        string screen = ScreenDir(Gravity);
        string next = screen switch { "up" => "right", "right" => "down", "down" => "left", _ => "up" };
        for (int i = 0; i < 4 && ScreenDir(Gravity) != next; i++) Gravity = (GridDir)(((int)Gravity + 1) % 4);
    }

    internal string ScreenDir(GridDir dir)
    {
        string grid = DirName(dir);
        return Input != null ? Input.ScreenDir(grid) : grid;
    }

    internal static string DirName(GridDir dir) => GasSimHud.WindDirections[(int)dir];

    internal void Saved(string directory, string name)
    {
        try { WaterFile.Save(WaterFile.PathFor(directory, name), Water); }
        catch (Exception error) { LiquidSandboxMod.Log.Warning("Water could not be saved: " + error.Message); }
    }

    internal void Loaded(string directory, string name)
    {
        var grid = Sim.Grid;
        if (grid == null) return;
        _pendingResize = false;
        Adopt(grid);
        Water.Clear();
        if (directory == null || name == null) return;
        try
        {
            if (WaterFile.Load(WaterFile.PathFor(directory, name), Water))
                LiquidSandboxMod.Log.Msg($"Loaded {Water.Total() / (double)LiquidGrid.Full:0.##} cells of water with '{name}'.");
        }
        catch (Exception error)
        {
            Water.Clear();
            LiquidSandboxMod.Log.Warning("Water could not be loaded: " + error.Message);
        }
    }

    internal void Fail(string part, Exception error)
    {
        if (_failed) return;
        _failed = true;
        LiquidSandboxMod.Log.Error($"Liquid Sandbox stopped ({part}): {error}");
    }
}
