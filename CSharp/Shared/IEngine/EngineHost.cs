using System;
using Barotrauma;
using HarmonyLib;
using IEngine.Diagnostics;
using Microsoft.Xna.Framework;

namespace IEngine;

public static class EngineHost
{
    public const string HarmonyId = "iengine";
    const string OwnerKey = "IEngine.Owner";

    static Harmony harmony;
    static object token;

    public static Harmony Harmony => harmony;

    public static bool Start(string root)
    {
        if (harmony != null) { return true; }
        object owner = AppDomain.CurrentDomain.GetData(OwnerKey);
        if (owner != null)
        {
            DebugConsole.NewMessage($"IEngine: already running from {owner}, this copy stays idle", Color.Orange);
            return false;
        }
        token = $"{typeof(EngineHost).Assembly.GetName().Name} ({root})";
        AppDomain.CurrentDomain.SetData(OwnerKey, token);
        harmony = new Harmony(HarmonyId);
        Settings.Load(root);
        Runtime.Install(harmony);
        Runtime.WatchForeignPatches(harmony);
        Runtime.SetMode(Settings.Mode);
        Commands.Register();
        Bench.Arm();
        Scenarios.Arm();
        DebugConsole.NewMessage($"IEngine ready: {Runtime.Describe()}", Color.LightGreen);
        return true;
    }

    public static void LoadCompleted()
    {
        if (harmony != null)
        {
            Runtime.OnLoadCompleted();
            if (Environment.GetEnvironmentVariable("IE_SEQ") is { Length: > 0 } seq)
            {
                Sequencer.Start(seq);
            }
        }
    }

    public static void Stop()
    {
        if (harmony == null) { return; }
        Commands.Unregister();
        Bench.Disarm();
        Scenarios.Disarm();
        Runtime.Uninstall();
        Probes.Uninstall();
        harmony.UnpatchSelf();
        harmony = null;
        if (ReferenceEquals(AppDomain.CurrentDomain.GetData(OwnerKey), token)) { AppDomain.CurrentDomain.SetData(OwnerKey, null); }
        token = null;
    }
}
