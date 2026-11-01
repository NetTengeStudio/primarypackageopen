using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;

namespace IEngine.Diagnostics;

static class Sequencer
{
    static readonly Queue<string> pending = new();

    public static string Start(string script)
    {
        pending.Clear();
        foreach (string c in script.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { pending.Enqueue(c); }
        Next();
        return $"sequence of {pending.Count + 1} steps";
    }

    public static void Next()
    {
        while (pending.TryDequeue(out string c))
        {
            DebugConsole.NewMessage($"IEngine seq > {c}", Microsoft.Xna.Framework.Color.LightGreen);
            DebugConsole.ExecuteCommand(c);
            if (Blocking) { return; }
        }
        if (Environment.GetEnvironmentVariable("IE_HOST_DONE_FILE") is { Length: > 0 } done) { File.WriteAllText(done, "done"); }
    }

    public static bool Blocking;
}
