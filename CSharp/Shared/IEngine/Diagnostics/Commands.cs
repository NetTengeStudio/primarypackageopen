using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace IEngine.Diagnostics;

static partial class Commands
{
    static readonly List<DebugConsole.Command> registered = new();

    public static void Register()
    {
        Add("ie_mode", "ie_mode [on|off|feature1,feature2]: switch IEngine features and remember the choice.", args =>
        {
            if (args.Length > 0)
            {
                Settings.Mode = args[0];
                Settings.Save();
                Runtime.SetMode(args[0]);
            }
            Say(Runtime.Describe());
        });
        Add("ie_takeover", "ie_takeover [on|off]: let IEngine run MapEntity.UpdateAll even when another mod transpiles it.", args =>
        {
            if (args.Length > 0)
            {
                Settings.Takeover = args[0] is "on" or "1" or "true";
                Settings.Save();
                Execution.ExecutorFeature.ForceRecheck();
            }
            Say($"takeover {(Settings.Takeover ? "on" : "off")}");
        });
        Add("ie_bench", "ie_bench [cycles=4] [seconds=20] [modes=off/on]: interleaved A/B measurement on the running scene.", args =>
        {
            int cycles = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 4;
            double seconds = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 20;
            string[] modes = (args.Length > 2 ? args[2] : "off/on").Split('/', StringSplitOptions.RemoveEmptyEntries);
            Say(Bench.Start(cycles, seconds, modes));
        });
        Add("ie_verify", "ie_verify [on|off]: run every IEngine fast path against its reference and count mismatches.", args =>
        {
            bool on = args.Length == 0 || args[0] != "off";
            foreach (Feature f in Runtime.Features) { f.SetVerify(on); }
            Say($"verify {(on ? "on" : "off")}");
        });
        Add("ie_seq", "ie_seq cmd1 | cmd2 | ...: run commands one after another, waiting for benches, shots and waits.", args => Say(Sequencer.Start(string.Join(" ", args))));
        Add("ie_wait", "ie_wait seconds: block an ie_seq for that many seconds of game time.", args =>
        {
            double until = Timing.TotalTime + (args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 5);
            Sequencer.Blocking = true;
            void Tick()
            {
                if (Timing.TotalTime < until) { return; }
                Probes.TickEnded -= Tick;
                Sequencer.Blocking = false;
                Sequencer.Next();
            }
            Probes.TickEnded += Tick;
        });
        Add("ie_itemprof", "ie_itemprof [fullTicks=600]: time every Item.Update (needs executor+activeset) and break the cost down by state, distance, prefab.", args =>
            Say(ItemProfile.Start(args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 600)));
        Add("ie_effects", "ie_effects prefab: dump the status effects of the first item of that prefab with IEngine's classification.", args =>
        {
            string text = Simulation.EffectDump.Of(args.Length > 0 ? args[0] : "");
            Say(text);
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Bench.OutDir(), "effects.txt"), text); } catch (System.IO.IOException) { }
        });
        Add("ie_scenario", "ie_scenario fire|flood|explosions|gunfire|npcs|physics|walk|stop [key=value]: reproducible load scenarios.", args => Say(Scenarios.Command(args)));
        RegisterSide();
        Add("ie_census", "ie_census: active items, updating components, scene size.", _ => Say(Census.Full()));
        Add("ie_report", "ie_report: per-feature diagnostics (also written to IE_OUT).", _ =>
        {
            var sb = new System.Text.StringBuilder(Census.Brief()).Append('\n');
            foreach (Feature f in Runtime.Features) { sb.Append($"{f.Name}: {(f.Installed ? (f.Enabled ? "on" : "off") : "unavailable " + f.Fault)} {f.Report()}\n"); }
            string text = sb.ToString();
            Say(text);
            try
            {
                System.IO.Directory.CreateDirectory(Bench.OutDir());
                System.IO.File.AppendAllText(System.IO.Path.Combine(Bench.OutDir(), "report.txt"), $"==== {DateTime.Now:O}\n{text}");
            }
            catch (System.IO.IOException) { }
        });
    }

    static partial void RegisterSide();

    public static void Unregister()
    {
        foreach (var c in registered) { DebugConsole.Commands.Remove(c); }
        registered.Clear();
    }

    static readonly HashSet<string> withoutProbes = new() { "ie_mode", "ie_takeover", "ie_report", "ie_census", "ie_effects" };

    internal static void Add(string name, string help, Action<string[]> run)
    {
        Action<string[]> body = withoutProbes.Contains(name) ? run : args =>
        {
            Probes.Ensure();
            run(args);
        };
        var command = new DebugConsole.Command(name, help, body);
#if CLIENT
        // Diagnostic commands operate on this client's engine state, not the server's.
        command.RelayToServer = false;
        command.OnClientExecute = body;
#endif
        DebugConsole.Commands.Add(command);
        registered.Add(command);
    }

    internal static void Say(string text)
    {
        foreach (string line in text.Split('\n')) { DebugConsole.NewMessage(line, Color.LightGreen); }
    }
}
