using Il2CppGasSim;
using System.Runtime.CompilerServices;
using MelonLoader;

[assembly: MelonInfo(typeof(LiquidSandbox.LiquidSandboxMod), "Liquid Sandbox", LiquidSandbox.LiquidSandboxMod.Version, "theop")]
[assembly: MelonGame("DefaultCompany", "EngineSimulator")]
[assembly: InternalsVisibleTo("LiquidSandbox.DevHarness")]

namespace LiquidSandbox;

public sealed class LiquidSandboxMod : MelonMod
{
    public const string Version = "1.2.0";

    internal static MelonLogger.Instance Log { get; private set; }

    /// <summary>True once every hook is in place. If any is missing the mod stays out of the way.</summary>
    public static bool Ready { get; private set; }

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        try
        {
            Hooks.Install(HarmonyInstance);
            Ready = true;
            Log.Msg("Liquid Sandbox ready: it is on the main menu under Sandbox.");
        }
        catch (Exception error)
        {
            HarmonyInstance.UnpatchSelf();
            Log.Error("Liquid Sandbox is turned off because this Combustor build does not match what it expects. " + error);
        }
    }

    public override void OnUpdate()
    {
        Hooks.NewFrame();
        LiquidSession.Current?.Update();
    }

    /// <summary>Leaves the main menu and opens Liquid Sandbox, like the menu button does.</summary>
    public static bool StartLiquidSandbox(MainMenu menu = null)
    {
        if (!Ready) return false;
        menu ??= UnityEngine.Object.FindFirstObjectByType<MainMenu>();
        if (menu != null) UnityEngine.Object.Destroy(menu.gameObject);
        if (UnityEngine.Object.FindFirstObjectByType<GasSimulation>() != null) return false;
        var sim = GasSimBootstrap.Create();
        if (sim == null) return false;
        LiquidSession.Begin(sim);
        return true;
    }
}
