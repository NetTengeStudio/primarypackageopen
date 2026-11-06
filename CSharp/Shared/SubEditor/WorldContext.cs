using System;
using Barotrauma;

namespace PrimaryPackage.SubEditor;

public readonly struct WorldContext : IDisposable
{
    [ThreadStatic] private static int depth;
    public static bool Active => depth != 0;
#if CLIENT
    private readonly Screen previous;

    private WorldContext(Screen previous) { this.previous = previous; }

    public static WorldContext Enter()
    {
        depth++;
        Screen previous = Screen.Selected is LiveEditorScreen ? Screen.Selected : null;
        if (previous != null) { Screen.Selected = GameMain.GameScreen; }
        return new WorldContext(previous);
    }

    public void Dispose()
    {
        depth--;
        if (previous != null) { Screen.Selected = previous; }
    }
#else
    public static WorldContext Enter() { depth++; return default; }
    public void Dispose() => depth--;
#endif
}
