using System.Text;
using Il2CppGasSim;
using Il2CppInterop.Runtime;
using LiquidSandbox.Simulation;
using UnityEngine;

namespace LiquidSandbox;

/// <summary>Liquid Sandbox panels, drawn with Combustor's own HUD styles and widgets.</summary>
internal sealed class LiquidHud
{
    public const string HintText =
        "Press <b>A</b> to pour water and <b>S</b> to drain it.\n" +
        "Press <b>W</b> to draw walls; water flows around them.\n" +
        "Set gravity under AMBIENT, or press <b>F</b> to turn it.";

    public const string ViewText =
        "Water: full cells are solid blue and darken with depth. Part-filled cells show their water level, " +
        "filled from the side gravity pulls towards. With no gravity they are tinted by how full they are.";

    const float StrengthStep = 0.05f;
    const string GravityTitle = "<b>Gravity</b>";
    const string StrengthTitle = "<color=#8c94a3>strength</color>";
    const string ZeroOn = "<b>Zero g</b>", ZeroOff = "Zero g", OneOn = "<b>1 g</b>", OneOff = "1 g";
    const string ZeroHint = "<color=#8c94a3>No gravity: water has no down, and neighbouring cells even out.</color>";
    const string LiveHint = "<color=#8c94a3>Changes apply to the water already in the grid.</color>";
    const string WaterHint = "<color=#8c94a3>A pours the picked liquid, S drains. Fuels float on water. Exposed surfaces evaporate until the air above is saturated; vapour shows as haze.</color>";
    static readonly string[] KindHex = { "", "#4ea8f5", "#fad660", "#cc923e" };
    const string EvapOn = "Evaporation: <b>on</b>", EvapOff = "Evaporation: off";
    const string Outside = "Cursor outside the grid";

    readonly LiquidSession _session;
    readonly Il2CppSystem.Func<string> _gravityDir;
    readonly Il2CppSystem.Action _turn;
    readonly StringBuilder _sb = new();
    string _inspectorText = "";
    int _inspectorKey = int.MinValue;
    long _inspectorTotal = -1;
    int _inspectorValue = -1;
    int _inspectorGravity = -1;
    string _totalText = "";
    long _totalShown = -1;
    string _strengthText = "1.00 g";
    float _strengthShown = 1f;

    public LiquidHud(LiquidSession session)
    {
        _session = session;
        _gravityDir = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<string>>(new Func<string>(() => LiquidSession.DirName(_session.Gravity)));
        _turn = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => _session.Gravity = (GridDir)(((int)_session.Gravity + 1) % 4)));
    }

    static string Dim(string text) => "<color=#8c94a3>" + text + "</color>";

    /// <summary>Takes the wind panel's place: the same direction picker the wind uses, then a
    /// strength slider in the style of the game's sound slider.</summary>
    public void DrawGravityPanel(GasSimHud hud)
    {
        GUILayout.Label(GravityTitle, hud._label, Gui.None);
        // Combustor's wind control: four arrow buttons in screen directions, the current one lit.
        hud.DirectionPicker("Pulls", "pulls", _gravityDir, _turn);

        float strength = _session.Strength;
        if (strength != _strengthShown)
        {
            _strengthShown = strength;
            _strengthText = strength <= 0f ? "0 g" : strength.ToString("0.00") + " g";
        }
        GUILayout.BeginHorizontal(Gui.None);
        GUILayout.Label(StrengthTitle, hud._label, Gui.TitleWidth);
        float picked = SimSkin.Slider(strength, 0f, LiquidGrid.MaxStrength, Gui.Expand);
        GUILayout.Label(_strengthText, hud._label, Gui.ValueWidth);
        GUILayout.EndHorizontal();
        picked = Mathf.Round(picked / StrengthStep) * StrengthStep;
        if (picked < StrengthStep * 0.5f) picked = 0f;
        if (Math.Abs(picked - strength) > 0.0001f) _session.Strength = picked;

        GUILayout.BeginHorizontal(Gui.None);
        if (SimGui.Button(strength <= 0f ? ZeroOn : ZeroOff, hud._button, Gui.RowHeight)) _session.Strength = 0f;
        if (SimGui.Button(Math.Abs(strength - 1f) < 0.001f ? OneOn : OneOff, hud._button, Gui.RowHeight)) _session.Strength = 1f;
        GUILayout.EndHorizontal();

        GUILayout.Label(strength <= 0f ? ZeroHint : LiveHint, hud._dim, Gui.None);
    }

    /// <summary>Takes the background gas panel's place.</summary>
    public void DrawWaterPanel(GasSimHud hud)
    {
        var water = _session.Water;
        GUILayout.BeginHorizontal(Gui.None);
        for (int k = 1; k <= LiquidGrid.KindCount; k++)
        {
            if (SimGui.Button(KindLabels[k, _session.SelectedKind == k ? 1 : 0], hud._button, Gui.RowHeight))
            {
                _session.SelectedKind = k;
                _session.Input?.SelectTool(Tool.Gas);
            }
        }
        GUILayout.EndHorizontal();
        long total = water.Total();
        if (total != _totalShown)
        {
            _totalShown = total;
            var sb = new StringBuilder();
            for (int k = 1; k <= LiquidGrid.KindCount; k++)
            {
                long liquid = water.LiquidOf(k), vapor = water.VaporOf(k);
                if (liquid == 0 && vapor == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("<color=").Append(KindHex[k]).Append('>').Append(LiquidGrid.Names[k].ToLowerInvariant()).Append("</color> <b>")
                  .Append((liquid / (double)LiquidGrid.Full).ToString("0.00")).Append("</b>");
                if (vapor > 0) sb.Append(Dim("  vapour ")).Append((vapor / (double)LiquidGrid.Full).ToString("0.00"));
                sb.Append(Dim(" cells"));
            }
            _totalText = sb.Length == 0 ? Dim("no liquid yet") : sb.ToString();
        }
        GUILayout.Label(_totalText, hud._label, Gui.None);
        GUILayout.BeginHorizontal(Gui.None);
        if (SimGui.Button(water.Evaporation ? EvapOn : EvapOff, hud._button, Gui.RowHeight)) water.Evaporation = !water.Evaporation;
        if (SimGui.Button("Drain all", hud._button, Gui.RowHeight)) _session.ClearWater();
        GUILayout.EndHorizontal();
        GUILayout.Label(WaterHint, hud._dim, Gui.None);
    }

    static readonly string[,] KindLabels = BuildKindLabels();

    static string[,] BuildKindLabels()
    {
        var labels = new string[LiquidGrid.KindCount + 1, 2];
        for (int k = 1; k <= LiquidGrid.KindCount; k++)
        {
            labels[k, 0] = "<color=" + KindHex[k] + ">" + LiquidGrid.Names[k] + "</color>";
            labels[k, 1] = "<b>[ <color=" + KindHex[k] + ">" + LiquidGrid.Names[k] + "</color> ]</b>";
        }
        return labels;
    }

    /// <summary>Same box, place and text style as the game's cell inspector.</summary>
    public void DrawInspector(GasSimHud hud)
    {
        var input = _session.Input;
        var water = _session.Water;
        string text;
        if (input == null || !input.HoverValid || !water.InBounds(input.HoverX, input.HoverY))
        {
            text = Outside;
            _inspectorKey = int.MinValue;
        }
        else
        {
            int x = input.HoverX, y = input.HoverY;
            int key = y * water.Width + x;
            int value = water.Get(x, y) | (water.IsSolid(x, y) ? 1 << 30 : 0);
            long total = water.Total();
            int gravity = water.Strength > 0f ? (int)water.Gravity : -2;
            if (key != _inspectorKey || value != _inspectorValue || total != _inspectorTotal || gravity != _inspectorGravity)
            {
                _inspectorGravity = gravity;
                _inspectorKey = key;
                _inspectorValue = value;
                _inspectorTotal = total;
                _inspectorText = Describe(x, y, total);
            }
            text = _inspectorText;
        }
        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        float screenH = SimSkin.ScreenH;
        float height = Math.Min(Math.Min(screenH * 0.34f, 300f), lines * 15f + 22f);
        var rect = new Rect(114f, screenH - height - 12f, 320f, height);
        hud._inspectRect = rect;
        SimSkin.Box(rect);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f), text, hud._label);
    }

    string Describe(int x, int y, long total)
    {
        var water = _session.Water;
        var sb = _sb;
        sb.Clear();
        sb.Append("<b>Cell ").Append(x).Append(", ").Append(y).Append("</b>\n");
        if (water.IsSolid(x, y))
        {
            sb.Append(WallName(x, y)).Append(Dim("  blocks water"));
        }
        else
        {
            int amount = water.Get(x, y);
            if (amount == 0)
            {
                sb.Append(Dim("empty"));
                for (int k = 1; k <= LiquidGrid.KindCount; k++)
                {
                    int v = water.VaporAt(x, y, k);
                    if (v > 0) sb.Append("   ").Append(LiquidGrid.Names[k].ToLowerInvariant()).Append(Dim(" vapour "))
                        .Append((100.0 * v / LiquidGrid.Saturation[k]).ToString("0")).Append(Dim("% saturated"));
                }
            }
            else
            {
                int kind = water.KindAt(x, y);
                double fill = amount / (double)LiquidGrid.Full;
                sb.Append("<color=").Append(KindHex[kind]).Append('>').Append(LiquidGrid.Names[kind].ToLowerInvariant()).Append("</color> <b>")
                  .Append((fill * 100).ToString(fill < 0.1 ? "0.0" : "0")).Append("%</b>");
                if (water.Strength > 0f)
                {
                    float depth = water.DepthAt(x, y);
                    if (depth >= 1f) sb.Append(Dim("   depth ")).Append(depth.ToString("0.0"));
                    sb.Append(Dim("   pressure ")).Append(water.PressureAt(x, y).ToString("0.0")).Append(Dim(" cells of water"));
                }
            }
        }
        sb.Append('\n').Append(Dim("all liquid and vapour ")).Append((total / (double)LiquidGrid.Full).ToString("0.00")).Append(Dim(" cells"));
        return sb.ToString();
    }

    string WallName(int x, int y)
    {
        try
        {
            var grid = _session.Sim.Grid;
            int m = grid.WallMaterialAt(grid.Index(x, y));
            var walls = grid.Walls;
            if (walls != null && m >= 0 && m < walls.Length) return walls[m].DisplayName.ToUpperInvariant();
        }
        catch (Exception)
        {
        }
        return "WALL";
    }

    static readonly GasSimHud.DockItem[] HiddenDock =
    {
        GasSimHud.DockItem.Probe, GasSimHud.DockItem.View, GasSimHud.DockItem.Feeds, GasSimHud.DockItem.Ambient,
        GasSimHud.DockItem.Edges, GasSimHud.DockItem.Experimental, GasSimHud.DockItem.Script, GasSimHud.DockItem.Engine,
        GasSimHud.DockItem.Codex,
    };

    /// <summary>Removes dock buttons for gas-only features (probe, views, feeds, ambient gas, open
    /// edges, script, engine, reactions) and the separators left doubled by that. The dock list holds
    /// (item, tool) pairs of two ints; they are edited in place in the game's list.</summary>
    public static unsafe void FilterDock(GasSimHud hud)
    {
        if (!Dock(hud, out int* data, out int* size, out int* version)) return;
        int n = *size, kept = 0;
        for (int i = 0; i < n; i++)
        {
            var item = (GasSimHud.DockItem)data[2 * i];
            if (Array.IndexOf(HiddenDock, item) >= 0) continue;
            bool separator = item == GasSimHud.DockItem.Separator;
            if (separator && (kept == 0 || (GasSimHud.DockItem)data[2 * (kept - 1)] == GasSimHud.DockItem.Separator)) continue;
            data[2 * kept] = data[2 * i];
            data[2 * kept + 1] = data[2 * i + 1];
            kept++;
        }
        while (kept > 0 && (GasSimHud.DockItem)data[2 * (kept - 1)] == GasSimHud.DockItem.Separator) kept--;
        if (kept == n) return;
        for (int i = kept; i < n; i++) data[2 * i] = data[2 * i + 1] = 0;
        *size = kept;
        (*version)++;
    }

    /// <summary>The dock entries, for checks.</summary>
    internal static unsafe List<(GasSimHud.DockItem item, Tool tool)> ReadDock(GasSimHud hud)
    {
        var list = new List<(GasSimHud.DockItem, Tool)>();
        if (!Dock(hud, out int* data, out int* size, out _)) return list;
        for (int i = 0; i < *size; i++) list.Add(((GasSimHud.DockItem)data[2 * i], (Tool)data[2 * i + 1]));
        return list;
    }

    static int _itemsOffset = -1, _sizeOffset, _versionOffset;

    static unsafe bool Dock(GasSimHud hud, out int* data, out int* size, out int* version)
    {
        data = size = version = null;
        var items = hud._dockItems;
        if (items == null) return false;
        byte* list = (byte*)items.Pointer;
        if (_itemsOffset < 0)
        {
            IntPtr klass = IL2CPP.il2cpp_object_get_class((IntPtr)list);
            _sizeOffset = FieldOffset(klass, "_size");
            _versionOffset = FieldOffset(klass, "_version");
            _itemsOffset = FieldOffset(klass, "_items");
        }
        byte* array = *(byte**)(list + _itemsOffset);
        if (array == null) return false;
        size = (int*)(list + _sizeOffset);
        version = (int*)(list + _versionOffset);
        long capacity = *(long*)(array + 3 * IntPtr.Size);
        if (*size < 0 || *size > capacity) return false;
        data = (int*)(array + 4 * IntPtr.Size);
        return true;
    }

    static int FieldOffset(IntPtr klass, string name)
    {
        IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
        if (field == IntPtr.Zero) throw new MissingFieldException("List", name);
        return (int)IL2CPP.il2cpp_field_get_offset(field);
    }
}
