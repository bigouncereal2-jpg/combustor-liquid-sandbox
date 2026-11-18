using System.Runtime.InteropServices;
using System.Text;
using Il2CppGasSim;
using LiquidSandbox;
using LiquidSandbox.Simulation;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(LiquidSandboxDev.Harness), "Liquid Sandbox dev harness", "0.1", "dev")]
[assembly: MelonGame("DefaultCompany", "EngineSimulator")]

namespace LiquidSandboxDev;

/// <summary>Drives Combustor through regular Sandbox and Liquid Sandbox, takes screenshots and
/// writes a report to UserData\LiquidSandboxDev. Development only.</summary>
public sealed class Harness : MelonMod
{
    static string Out => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "LiquidSandboxDev");
    readonly StringBuilder _log = new();
    readonly List<(float at, string name, Action act)> _steps = new();
    int _next;
    float _start = -1, _t;
    readonly List<float> _frames = new();
    bool _recordFrames;
    readonly List<float> _solverMs = new();
    long _allocStart, _allocFrames;
    int _hoverX = -1, _hoverY;
    long _expectedWater = -1;
    bool _watchWater;
    int _waterChecks, _waterBad;

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    int _clickX, _clickY;

    static void MoveTo(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x8001, (uint)(x * 65535 / Math.Max(1, Screen.width - 1)), (uint)(y * 65535 / Math.Max(1, Screen.height - 1)), 0, UIntPtr.Zero);
    }
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    void W(string s)
    {
        string line = $"[{_t,6:0.0}] {s}";
        _log.AppendLine(line);
        LoggerInstance.Msg(line);
        File.WriteAllText(Path.Combine(Out, "report.txt"), _log.ToString());
    }

    void Check(bool ok, string what) => W((ok ? "PASS " : "FAIL ") + what);

    void Shot(string name) => MelonCoroutines.Start(ShotCo(Path.Combine(Out, name + ".png")));

    System.Collections.IEnumerator ShotCo(string path)
    {
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
        UnityEngine.Object.Destroy(tex);
    }

    void Step(float after, string name, Action act) => _steps.Add(((_steps.Count == 0 ? 0 : _steps[^1].at) + after, name, act));

    static GasSimulation Sim => UnityEngine.Object.FindFirstObjectByType<GasSimulation>();
    static GasSimInput Input => UnityEngine.Object.FindFirstObjectByType<GasSimInput>();
    static double Cells(long units) => units / (double)LiquidGrid.Full;

    public override void OnInitializeMelon()
    {
        Directory.CreateDirectory(Out);
        foreach (var f in Directory.GetFiles(Out)) File.Delete(f);

        Step(6, "menu", () =>
        {
            Check(LiquidSandboxMod.Ready, "mod hooks installed");
            Check(UnityEngine.Object.FindFirstObjectByType<MainMenu>() != null, "main menu up");
            Check(Hooks.MenuButtonFrame > 0, $"Liquid Sandbox button drawn at {Hooks.MenuButtonRect}");
            Shot("01_menu");
        });
        Step(2, "regular sandbox", () => UnityEngine.Object.FindFirstObjectByType<MainMenu>().StartSandbox());
        Step(4, "regular sandbox check", () =>
        {
            var sim = Sim;
            Check(sim != null && LiquidSession.Current == null, "regular Sandbox opened without a liquid session");
            Check(L.Data("Gas") == "Gas" && L.Data("Vacuum") == "Vacuum", "tool names are the game's");
            Check(!sim.settings.vacuumAmbient, "ambient air as usual");
            var input = Input;
            input.SelectTool(Tool.Gas);
            Check(input.tool == Tool.Gas, "gas tool selectable");
            int cx = sim.Grid.Width / 2, cy = sim.Grid.Height / 2;
            double before = 0, after = 0;
            foreach (float a in sim.Grid.Amount) before += a;
            for (int k = 0; k < 10; k++) input.StampOne(cx, cy, false);
            foreach (float a in sim.Grid.Amount) after += a;
            W($"gas in grid {before:0.000} -> {after:0.000}");
            Check(after > before, "gas brush adds gas in regular Sandbox");
            input.SelectTool(Tool.Wall);
            Shot("02_regular_sandbox");
            _frames.Clear(); _solverMs.Clear(); _recordFrames = true; _allocStart = GC.GetTotalAllocatedBytes(); _allocFrames = 0;
        });
        Step(5, "regular sandbox perf", () =>
        {
            _recordFrames = false;
            Report("regular Sandbox");
            W($"ticks/s {Sim.MeasuredTicksPerSecond:0.0}");
            GasSimBootstrap.ReturnToMenu();
        });
        Step(4, "click Liquid Sandbox", () =>
        {
            Shot("03_menu_again");
            var p = Hooks.MenuButtonScreenCentre;
            float scale = Screen.height / SimSkin.ScreenH;
            _clickX = (int)(p.x * scale); _clickY = (int)(p.y * scale);
            W($"menu button centre {p} in GUI points, scale {scale:0.000}: clicking screen {_clickX},{_clickY}");
            MoveTo(_clickX, _clickY);
        });
        Step(0.5f, "press", () => { MoveTo(_clickX, _clickY); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); });
        Step(0.15f, "release", () => mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero));
        Step(3, "liquid sandbox opened", () =>
        {
            bool ok = LiquidSession.Current != null;
            W((ok ? "first click opened Liquid Sandbox" : "first click missed") + $" (session {ok})");
            if (!ok && Sim != null) GasSimBootstrap.ReturnToMenu();
        });
        Step(3, "second click", () =>
        {
            if (LiquidSession.Current != null) return;
            _clickX = Screen.width / 2; _clickY = (int)(Screen.height * 0.3231f);
            MoveTo(_clickX, _clickY);
        });
        Step(0.5f, "press2", () => { if (LiquidSession.Current == null) { MoveTo(_clickX, _clickY); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); } });
        Step(0.15f, "release2", () => { if (LiquidSession.Current == null) mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); });
        Step(2.5f, "fallback start", () =>
        {
            Check(LiquidSession.Current != null, "Liquid Sandbox opened by a real mouse click on its menu button");
            if (LiquidSession.Current == null) { if (Sim != null) GasSimBootstrap.ReturnToMenu(); }
        });
        Step(2.5f, "fallback start 2", () => { if (LiquidSession.Current == null) LiquidSandboxMod.StartLiquidSandbox(); });
        Step(1.5f, "liquid checks", () =>
        {
            var s = LiquidSession.Current;
            Check(s != null && s.Sim.settings.vacuumAmbient && !s.Sim.settings.openBoundaries, "no gas, sealed edges");
            Check(L.Data("Gas") == "Water" && L.Data("Vacuum") == "Drain", "gas tools shown as Water and Drain");
            Check(!s.Input.ToolAllowed(Tool.Heat) && s.Input.ToolAllowed(Tool.Gas) && s.Input.ToolAllowed(Tool.Wall), "tool set limited to walls, water, drain, valve");
            MoveTo(Screen.width - 20, Screen.height / 2);
            Shot("04_liquid_empty");
        });
        Step(0.5f, "click gravity left arrow", () => { _clickX = Screen.width * 267 / 1920; _clickY = Screen.height * 629 / 1080; MoveTo(_clickX, _clickY); });
        Step(0.4f, "press arrow", () => { MoveTo(_clickX, _clickY); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); });
        Step(0.15f, "release arrow", () => mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero));
        Step(0.5f, "arrow result", () =>
        {
            var s = LiquidSession.Current;
            Check(s.Gravity == GridDir.Left, $"clicking the left arrow of the gravity picker turns gravity left (now {s.Gravity})");
            Shot("04b_gravity_panel_left");
            keybd_event(0x46, 0x21, 0, UIntPtr.Zero);
        });
        Step(0.15f, "F up", () => keybd_event(0x46, 0x21, 2, UIntPtr.Zero));
        Step(0.5f, "F result", () =>
        {
            var s = LiquidSession.Current;
            Check(s.Gravity == GridDir.Up, $"F turns gravity clockwise on screen, left -> up (now {s.Gravity})");
            s.Gravity = GridDir.Down;
            MoveTo(Screen.width - 20, Screen.height / 2);
        });
        Step(1, "build scene", () =>
        {
            var s = LiquidSession.Current;
            var g = s.Sim.Grid;
            // cup
            for (int x = 20; x <= 80; x++) g.SetSolid(x, 20, true, 1);
            for (int y = 20; y <= 62; y++) { g.SetSolid(20, y, true, 1); g.SetSolid(80, y, true, 1); }
            // U tube: carve two legs and a channel out of a block of steel
            for (int y = 10; y <= 112; y++) for (int x = 140; x <= 216; x++) g.SetSolid(x, y, true, 2);
            for (int y = 12; y <= 110; y++) for (int x = 146; x <= 153; x++) g.SetSolid(x, y, false);
            for (int y = 12; y <= 110; y++) for (int x = 203; x <= 210; x++) g.SetSolid(x, y, false);
            for (int y = 12; y <= 19; y++) for (int x = 146; x <= 210; x++) g.SetSolid(x, y, false);
            // a slanted shelf above the cup, so poured water runs around it
            for (int k = 0; k < 18; k++) g.SetSolid(38 + k, 100 - k / 2, true, 3);
            s.Water.SyncSolid(g.Solid.AsSpan());
            for (int y = 12; y <= 19; y++) for (int x = 146; x <= 210; x++) s.Water.Add(x, y, LiquidGrid.Full);
            for (int y = 20; y <= 100; y++) for (int x = 146; x <= 153; x++) s.Water.Add(x, y, LiquidGrid.Full);
            W($"U tube filled: {Cells(s.Water.Total()):0.00} cells");
        });
        int pours = 0;
        Step(0.2f, "pour with the water brush", () =>
        {
            var s = LiquidSession.Current;
            s.Input.SelectTool(Tool.Gas);
            long before = s.Water.Total();
            for (int k = 0; k < 120; k++) { s.Input.StampOne(48, 120, false); pours++; }
            W($"water brush: {Cells(before):0.00} -> {Cells(s.Water.Total()):0.00} cells");
            Check(s.Water.Total() > before, "Gas tool pours water in Liquid Sandbox");
        });
        Step(0.5f, "pour more", () =>
        {
            var s = LiquidSession.Current;
            for (int k = 0; k < 400; k++) s.Input.StampOne(46 + k % 6, 125, false);
            s.Input.SelectTool(Tool.Wall);
            _expectedWater = s.Water.Total();
            _watchWater = true;
            W($"poured; total {Cells(_expectedWater):0.00} cells");
        });
        Step(0.6f, "falling", () =>
        {
            Shot("05_pouring");
            var dock = LiquidHud.ReadDock(LiquidSession.Current.GameHud);
            W("liquid dock: " + string.Join(",", dock.Select(d => d.item == GasSimHud.DockItem.Tool ? d.tool.ToString() : d.item.ToString())));
            Check(!dock.Any(d => d.item == GasSimHud.DockItem.Codex || d.item == GasSimHud.DockItem.Engine || d.tool == Tool.Heat), "liquid dock without gas-only buttons");
        });
        Step(8, "settled", () =>
        {
            var s = LiquidSession.Current;
            Shot("06_settled");
            int left = Level(s.Water, 150, 12, 110), right = Level(s.Water, 206, 12, 110);
            W($"U tube levels (unit rows): left {left / 65536.0:0.00} right {right / 65536.0:0.00}");
            Check(Math.Abs(left - right) < LiquidGrid.Full, "U tube legs at one level");
            double inCup = 0;
            for (int x = 21; x < 80; x++) for (int y = 21; y <= 62; y++) inCup += s.Water.Get(x, y);
            W($"water in the cup {Cells((long)inCup):0.00} cells");
        });
        Step(1, "gravity left", () =>
        {
            LiquidSession.Current.Gravity = GridDir.Left;
            _frames.Clear(); _solverMs.Clear(); _recordFrames = true; _allocStart = GC.GetTotalAllocatedBytes(); _allocFrames = 0;
        });
        Step(0.5f, "flowing left", () => Shot("07_gravity_left_flowing"));
        Step(5, "settled left", () => { Shot("08_gravity_left_settled"); _recordFrames = false; Report("Liquid Sandbox, water moving"); });
        Step(1, "zero g", () => LiquidSession.Current.Strength = 0f);
        Step(6, "zero g shot", () => Shot("09_zero_g"));
        Step(1, "gravity up, weak", () => { LiquidSession.Current.Gravity = GridDir.Up; LiquidSession.Current.Strength = 0.3f; });
        Step(4, "up shot", () => Shot("10_gravity_up_weak"));
        Step(1, "gravity down", () => { LiquidSession.Current.Gravity = GridDir.Down; LiquidSession.Current.Strength = 1f; });
        Step(8, "down settled", () =>
        {
            var s = LiquidSession.Current;
            Shot("11_down_again");
            // paint a wall stroke through water: displaced water must stay in the grid
            long before = s.Water.Total();
            s.Input.SelectTool(Tool.Wall);
            for (int x = 30; x < 60; x++) s.Input.StampOne(x, 24, false);
            s.Input.CloseStroke();
            W($"wall painted through water; total before {Cells(before):0.00}");
        });
        Step(1, "after wall", () =>
        {
            var s = LiquidSession.Current;
            Check(s.Water.Total() == _expectedWater, $"water kept after painting walls through it ({Cells(s.Water.Total()):0.00} vs {Cells(_expectedWater):0.00})");
            // hover the cursor over the water for the inspector
            _hoverX = 50; _hoverY = 22;
        });
        Step(0.5f, "resize", () =>
        {
            var s = LiquidSession.Current;
            _expectedWater = s.Water.Total();
            bool ok = s.Sim.SetSpace(1);
            var g = s.Sim.Grid;
            int minX = int.MaxValue, minY = int.MaxValue;
            for (int y = 0; y < g.Height; y++) for (int x = 0; x < g.Width; x++)
                if (g.Solid[y * g.Width + x] == 3) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); }
            int dx = minX - 140, dy = minY - 10;
            W($"grid size change ok={ok}: now {g.Width}x{g.Height}, walls moved by {dx},{dy}");
            Check(s.Water.Width == g.Width && s.Water.Height == g.Height, "water grid follows the new size");
            Check(s.Water.Get(150 + dx, 50 + dy) == LiquidGrid.Full && s.Water.Get(206 + dx, 50 + dy) == LiquidGrid.Full, "water moved with the walls");
            Check(s.Water.Total() == _expectedWater, "water kept through the size change");
        });
        Step(2, "resize back", () =>
        {
            var s = LiquidSession.Current;
            Shot("12c_bigger_grid");
            s.Sim.SetSpace(0);
            Check(s.Water.Total() == _expectedWater && s.Water.Width == 256, "water kept after changing back");
        });
        Step(1.5f, "inspector", () =>
        {
            var i = LiquidSession.Current.Input;
            W($"hover valid {i.HoverValid} at {i.HoverX},{i.HoverY}, water there {LiquidSession.Current.Water.Get(i.HoverX, i.HoverY) / 65536.0:0.00}");
            Shot("12_inspector");
            _hoverX = -1;
            var r = LiquidSession.Current.Renderer;
            r.ZoomAt(10f, new Vector2(Screen.width * 0.63f, Screen.height * 0.497f));
        });
        Step(1, "zoomed", () => { Shot("12b_zoomed_surface"); LiquidSession.Current.Renderer.ResetView(); });
        Step(1, "save", () =>
        {
            var s = LiquidSession.Current;
            _watchWater = false;
            bool ok = s.Sim.SaveState("Liquid Sandbox selftest");
            string path = Path.Combine(GasSimulation.SaveDirectory, "Liquid Sandbox selftest.engine.water");
            Check(ok && File.Exists(path), "save writes the water beside the engine file");
            s.ClearWater();
            bool loaded = s.Sim.LoadState("Liquid Sandbox selftest");
            Check(loaded && Math.Abs(s.Water.Total() - _expectedWater) < LiquidGrid.Full, $"load restores the water ({Cells(s.Water.Total()):0.00} cells)");
            _expectedWater = s.Water.Total();
            _watchWater = true;
            foreach (var f in new[] { path, Path.Combine(GasSimulation.SaveDirectory, "Liquid Sandbox selftest.engine") })
                if (File.Exists(f)) File.Delete(f);
        });
        Step(3, "back to menu", () =>
        {
            _watchWater = false;
            W($"water checks while running: {_waterChecks}, mismatches: {_waterBad}");
            Check(_waterChecks > 100 && _waterBad == 0, "water total constant every frame while nothing was poured or drained");
            GasSimBootstrap.ReturnToMenu();
        });
        Step(3, "regular again", () =>
        {
            Check(LiquidSession.Current == null, "liquid session ended on return to menu");
            Check(L.Data("Gas") == "Gas" && L.Data("Vacuum") == "Vacuum", "tool names restored");
            UnityEngine.Object.FindFirstObjectByType<MainMenu>().StartSandbox();
        });
        Step(4, "regular again check", () =>
        {
            var sim = Sim;
            Check(sim != null && LiquidSession.Current == null && !sim.settings.vacuumAmbient, "regular Sandbox normal after Liquid Sandbox");
            var hud = UnityEngine.Object.FindFirstObjectByType<GasSimHud>();
            var dock = LiquidHud.ReadDock(hud);
            W("regular dock: " + string.Join(",", dock.Select(d => d.item == GasSimHud.DockItem.Tool ? d.tool.ToString() : d.item.ToString())));
            Check(dock.Any(d => d.item == GasSimHud.DockItem.Codex) && dock.Any(d => d.tool == Tool.Heat), "regular dock complete");
            Shot("13_regular_again");
        });
        Step(2, "quit", () =>
        {
            W("done");
            Application.Quit();
        });
        Step(4, "kill", () => System.Diagnostics.Process.GetCurrentProcess().Kill());
    }

    static int Level(LiquidGrid w, int x, int y0, int y1)
    {
        int level = y0 * LiquidGrid.Full;
        for (int y = y0; y <= y1; y++) { int c = w.Get(x, y); if (c > 0) level = y * LiquidGrid.Full + c; }
        return level;
    }

    void Report(string what)
    {
        if (_frames.Count == 0) return;
        _frames.Sort();
        float avg = _frames.Average(), p99 = _frames[(int)(_frames.Count * 0.99f)], max = _frames[^1];
        W($"{what}: managed allocations {(GC.GetTotalAllocatedBytes() - _allocStart) / (double)Math.Max(1, _allocFrames) / 1024:0.0} KB per frame");
        W($"{what}: {_frames.Count} frames, avg {avg * 1000:0.00} ms ({1f / avg:0} fps), 99th percentile {p99 * 1000:0.00} ms, worst {max * 1000:0.00} ms, solver avg {(_solverMs.Count > 0 ? _solverMs.Average() : 0):0.000} ms/frame");
    }

    public override void OnLateUpdate()
    {
        var s = LiquidSession.Current;
        if (_hoverX >= 0 && s != null)
        {
            s.Input.HoverX = _hoverX;
            s.Input.HoverY = _hoverY;
            s.Input.HoverValid = true;
        }
    }

    public override void OnUpdate()
    {
        if (_start < 0) _start = Time.realtimeSinceStartup;
        _t = Time.realtimeSinceStartup - _start;
        if (_recordFrames)
        {
            _frames.Add(Time.unscaledDeltaTime);
            _allocFrames++;
            var sim = Sim;
            if (sim != null) _solverMs.Add(sim.SolverMilliseconds);
        }
        var s = LiquidSession.Current;
        if (_watchWater && s != null)
        {
            _waterChecks++;
            if (s.Water.Total() != _expectedWater)
            {
                if (_waterBad++ < 5) W($"water changed: {s.Water.Total()} vs {_expectedWater}");
                _expectedWater = s.Water.Total();
            }
        }
        while (_next < _steps.Count && _t >= _steps[_next].at)
        {
            var step = _steps[_next++];
            try { step.act(); }
            catch (Exception e) { W($"FAIL step '{step.name}' threw {e}"); }
        }
    }
}
