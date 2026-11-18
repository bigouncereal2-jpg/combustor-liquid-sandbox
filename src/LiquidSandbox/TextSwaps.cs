using Il2CppGasSim;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace LiquidSandbox;

/// <summary>Cached IMGUI layout options, so drawing does not allocate them every frame.</summary>
internal static class Gui
{
    public static readonly Il2CppReferenceArray<GUILayoutOption> None = new(0);
    public static readonly Il2CppReferenceArray<GUILayoutOption> MenuButtonHeight = Options(GUILayout.Height(44f));
    public static readonly Il2CppReferenceArray<GUILayoutOption> RowHeight = Options(GUILayout.Height(20f));
    public static readonly Il2CppReferenceArray<GUILayoutOption> TitleWidth = Options(GUILayout.Width(96f));
    public static readonly Il2CppReferenceArray<GUILayoutOption> ValueWidth = Options(GUILayout.Width(52f));
    public static readonly Il2CppReferenceArray<GUILayoutOption> Expand = Options(GUILayout.ExpandWidth(true));

    static Il2CppReferenceArray<GUILayoutOption> Options(params GUILayoutOption[] options)
    {
        var array = new Il2CppReferenceArray<GUILayoutOption>(options.Length);
        for (int i = 0; i < options.Length; i++) array[i] = options[i];
        return array;
    }
}

/// <summary>Renames the gas tools while Liquid Sandbox runs and puts the texts back afterwards.
/// Tool labels are looked up by their English name in the game's text table, so adding entries
/// there renames them everywhere the game shows them.</summary>
internal sealed class TextSwaps
{
    readonly List<(string key, string original)> _saved = new();
    IntPtr _table;

    static readonly (string key, string text)[] Texts =
    {
        ("Gas", "Water"),
        ("Vacuum", "Drain"),
        ("dock.tool.gas", "Pour water. The mouse wheel sets how fast it flows."),
        ("dock.tool.vacuum", "Drain water from under the brush. The mouse wheel sets how fast."),
    };

    public void Apply()
    {
        var table = L._text;
        if (table == null) return;
        _table = table.Pointer;
        foreach (var (key, text) in Texts)
        {
            _saved.Add((key, table.ContainsKey(key) ? table[key] : null));
            table[key] = text;
        }
    }

    public void Restore()
    {
        var table = L._text;
        // A language change loads a fresh table; then there is nothing to put back.
        if (table != null && table.Pointer == _table)
        {
            foreach (var (key, original) in _saved)
            {
                if (original == null) table.Remove(key);
                else table[key] = original;
            }
        }
        _saved.Clear();
        _table = IntPtr.Zero;
    }
}
