using System.Reflection;
using HarmonyLib;
using Il2CppGasSim;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace LiquidSandbox;

/// <summary>Harmony hooks into Combustor. Every hook returns straight to the game's own code unless
/// the object belongs to the running Liquid Sandbox, so the regular modes are unchanged.</summary>
internal static class Hooks
{
    static MainMenu _menu;
    static GUIContent _menuLabel;
    static GUIContent MenuLabel => _menuLabel ??= new GUIContent("Liquid Sandbox");
    static bool _menuButtonDone;
    static bool _inDrawTools;

    /// <summary>Menu button rectangle in its layout group, and its centre in screen points, from the last repaint.</summary>
    public static Rect MenuButtonRect { get; private set; }
    public static Vector2 MenuButtonScreenCentre { get; private set; }
    public static int MenuButtonFrame { get; private set; } = -1;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        void Patch(Type type, string name, string prefix, string postfix)
        {
            var target = AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);
            HarmonyMethod Hook(string method) => method == null ? null
                : new HarmonyMethod(typeof(Hooks).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(Hooks), method));
            harmony.Patch(target, Hook(prefix), Hook(postfix));
        }

        Patch(typeof(MainMenu), nameof(MainMenu.DrawRoot), nameof(DrawRootPrefix), nameof(DrawRootPostfix));
        Patch(typeof(SimEdition), "get_HasFeedTheHole", null, nameof(AfterSandboxButton));

        Patch(typeof(GasGrid), nameof(GasGrid.Step), nameof(StepPrefix), null);
        Patch(typeof(GasGrid), nameof(GasGrid.AddGas), nameof(AddGasPrefix), null);
        Patch(typeof(GasGrid), nameof(GasGrid.RemoveGas), nameof(RemoveGasPrefix), null);
        Patch(typeof(GasGrid), nameof(GasGrid.AddHeat), nameof(AddHeatPrefix), null);

        Patch(typeof(GasSimRenderer), nameof(GasSimRenderer.DrawGenerators), nameof(DrawGeneratorsPrefix), null);

        Patch(typeof(GasSimInput), nameof(GasSimInput.ToolAllowed), nameof(ToolAllowedPrefix), null);
        Patch(typeof(GasSimInput), nameof(GasSimInput.CycleView), nameof(InputOnlyOutsideLiquid), null);
        Patch(typeof(GasSimInput), nameof(GasSimInput.ToggleCodex), nameof(InputOnlyOutsideLiquid), null);
        Patch(typeof(GasSimInput), nameof(GasSimInput.HandleKeys), null, nameof(HandleKeysPostfix));
        Patch(typeof(GasSimInput), nameof(GasSimInput.OnGridReplaced), null, nameof(GridReplacedPostfix));

        Patch(typeof(GasSimHud), nameof(GasSimHud.DrawTools), nameof(DrawToolsPrefix), nameof(DrawToolsPostfix));
        Patch(typeof(GasSimHud), nameof(GasSimHud.DrawWindPanel), nameof(WindPanelPrefix), null);
        Patch(typeof(GasSimHud), nameof(GasSimHud.DrawBackgroundPanel), nameof(BackgroundPanelPrefix), null);
        Patch(typeof(GasSimHud), nameof(GasSimHud.ViewDescription), nameof(ViewDescriptionPrefix), null);
        Patch(typeof(GasSimHud), nameof(GasSimHud.DrawInspector), nameof(InspectorPrefix), null);
        Patch(typeof(GasSimHud), nameof(GasSimHud.BuildDockItems), null, nameof(DockItemsPostfix));

        Patch(typeof(GasSimulation), nameof(GasSimulation.ToggleVacuum), nameof(SimOnlyOutsideLiquid), null);
        Patch(typeof(GasSimulation), nameof(GasSimulation.SetBackgroundGas), nameof(SimOnlyOutsideLiquid), null);
        Patch(typeof(GasSimulation), nameof(GasSimulation.ResetGas), null, nameof(ResetGasPostfix));
        Patch(typeof(GasSimulation), nameof(GasSimulation.ResetAll), null, nameof(ResetAllPostfix));
        Patch(typeof(GasSimulation), nameof(GasSimulation.SaveTo), null, nameof(SaveToPostfix));
        Patch(typeof(GasSimulation), nameof(GasSimulation.LoadFrom), null, nameof(LoadFromPostfix));
        Patch(typeof(GasSimulation), nameof(GasSimulation.LoadPacked), null, nameof(LoadPackedPostfix));
        Patch(typeof(GasSimulation), nameof(GasSimulation.OnDestroy), nameof(SimDestroyPrefix), null);
    }

    public static void NewFrame() => _inDrawTools = false;

    // ------------------------------------------------------------ main menu

    static void DrawRootPrefix(MainMenu __instance)
    {
        _menu = __instance;
        _menuButtonDone = false;
    }

    static void DrawRootPostfix() => _menu = null;

    // DrawRoot asks for this right after drawing the Sandbox button and its spacing, so the
    // Liquid Sandbox button lands directly below Sandbox, in the same column and style.
    static void AfterSandboxButton()
    {
        var menu = _menu;
        if (menu == null || _menuButtonDone) return;
        _menuButtonDone = true;
        try
        {
            var rect = GUILayoutUtility.GetRect(MenuLabel, menu._button, Gui.MenuButtonHeight);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                MenuButtonRect = rect;
                MenuButtonScreenCentre = GUIUtility.GUIToScreenPoint(rect.center);
                MenuButtonFrame = Time.frameCount;
            }
            if (SimGui.Button(rect, MenuLabel, menu._button))
            {
                _menu = null;
                LiquidSandboxMod.StartLiquidSandbox(menu);
            }
            GUILayout.Space(8f);
        }
        catch (Exception error)
        {
            LiquidSandboxMod.Log.Error(error.ToString());
        }
    }

    // ------------------------------------------------------------ simulation

    static LiquidSession For(GasGrid grid)
    {
        var s = LiquidSession.Current;
        return s != null && s.Owns(grid) ? s : null;
    }

    static bool StepPrefix(GasGrid __instance)
    {
        var s = LiquidSession.Current;
        if (s == null) return true;
        return !s.TryTick(__instance);
    }

    static bool AddGasPrefix(GasGrid __instance, int x, int y, float amount)
    {
        var s = For(__instance);
        if (s == null) return true;
        s.Pour(x, y, amount);
        return false;
    }

    static bool RemoveGasPrefix(GasGrid __instance, int x, int y, float fraction)
    {
        var s = For(__instance);
        if (s == null) return true;
        s.Drain(x, y, fraction);
        return false;
    }

    static bool AddHeatPrefix(GasGrid __instance) => For(__instance) == null;

    static void DrawGeneratorsPrefix(GasGrid grid, Il2CppStructArray<Color32> pixels)
    {
        var s = For(grid);
        if (s == null) return;
        try { s.Render(pixels); }
        catch (Exception error) { s.Fail("drawing", error); }
    }

    static void GridReplacedPostfix(GasSimInput __instance, int dx, int dy)
    {
        var s = LiquidSession.Current;
        if (s != null && s.Owns(__instance)) s.GridReplaced(dx, dy);
    }

    static void ResetGasPostfix(GasSimulation __instance)
    {
        var s = LiquidSession.Current;
        if (s != null && s.Owns(__instance)) s.ClearWater();
    }

    static void ResetAllPostfix(GasSimulation __instance)
    {
        var s = LiquidSession.Current;
        if (s != null && s.Owns(__instance)) s.ClearWater();
    }

    static void SaveToPostfix(GasSimulation __instance, string directory, string name, bool __result)
    {
        var s = LiquidSession.Current;
        if (__result && s != null && s.Owns(__instance)) s.Saved(directory, name);
    }

    static void LoadFromPostfix(GasSimulation __instance, string directory, string name, bool __result)
    {
        var s = LiquidSession.Current;
        if (__result && s != null && s.Owns(__instance)) s.Loaded(directory, name);
    }

    // Share codes and Workshop engines carry walls only.
    static void LoadPackedPostfix(GasSimulation __instance, bool __result)
    {
        var s = LiquidSession.Current;
        if (__result && s != null && s.Owns(__instance)) s.Loaded(null, null);
    }

    static void SimDestroyPrefix(GasSimulation __instance)
    {
        var s = LiquidSession.Current;
        if (s != null && s.Owns(__instance)) LiquidSession.End();
    }

    static bool SimOnlyOutsideLiquid(GasSimulation __instance)
    {
        var s = LiquidSession.Current;
        return s == null || !s.Owns(__instance);
    }

    // ------------------------------------------------------------ input

    static bool InputOnlyOutsideLiquid(GasSimInput __instance)
    {
        var s = LiquidSession.Current;
        return s == null || !s.Owns(__instance);
    }

    static bool ToolAllowedPrefix(GasSimInput __instance, Tool t, ref bool __result)
    {
        var s = LiquidSession.Current;
        if (s == null || !s.Owns(__instance)) return true;
        // While the tools panel is drawn, report the gas tools as unavailable so it skips its
        // gas palette; the water brush still uses the Gas tool everywhere else.
        if (LiquidSession.ToolAllowed(t) && !(_inDrawTools && t is Tool.Gas or Tool.Generator)) return true;
        __result = false;
        return false;
    }

    static void HandleKeysPostfix(GasSimInput __instance)
    {
        var s = LiquidSession.Current;
        if (s != null && s.Owns(__instance)) s.HandleKeys();
    }

    // ------------------------------------------------------------ HUD

    static void DrawToolsPrefix(GasSimHud __instance)
    {
        var s = LiquidSession.Current;
        _inDrawTools = s != null && s.Owns(__instance);
    }

    static void DrawToolsPostfix() => _inDrawTools = false;

    static bool WindPanelPrefix(GasSimHud __instance)
    {
        var s = LiquidSession.Current;
        if (s == null || !s.Owns(__instance)) return true;
        _inDrawTools = false;
        try { s.Hud.DrawGravityPanel(__instance); }
        catch (Exception error) { s.Fail("gravity panel", error); }
        return false;
    }

    static bool BackgroundPanelPrefix(GasSimHud __instance)
    {
        var s = LiquidSession.Current;
        if (s == null || !s.Owns(__instance)) return true;
        try { s.Hud.DrawWaterPanel(__instance); }
        catch (Exception error) { s.Fail("water panel", error); }
        return false;
    }

    static bool ViewDescriptionPrefix(GasSimHud __instance, ref string __result)
    {
        var s = LiquidSession.Current;
        if (s == null || !s.Owns(__instance)) return true;
        __result = LiquidHud.ViewText;
        return false;
    }

    static bool InspectorPrefix(GasSimHud __instance)
    {
        var s = LiquidSession.Current;
        if (s == null || !s.Owns(__instance)) return true;
        try { s.Hud.DrawInspector(__instance); }
        catch (Exception error) { s.Fail("inspector", error); }
        return false;
    }

    static void DockItemsPostfix(GasSimHud __instance)
    {
        var s = LiquidSession.Current;
        if (s == null || !s.Owns(__instance)) return;
        try { LiquidHud.FilterDock(__instance); }
        catch (Exception error) { s.Fail("dock", error); }
    }
}
