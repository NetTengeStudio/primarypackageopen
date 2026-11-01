using System;
using System.Globalization;
using System.IO;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace IEngine.Diagnostics;

static class FrameShot
{
    const int SettleFrames = 12;

    static string[] plan;
    static string feature, tag;
    static bool twin;
    static int step, wait;
    static Color[] reference, current;
    static readonly System.Text.StringBuilder log = new();

    public static string Start(string featureName, string label, bool sameFrame)
    {
        if (plan != null) { return "shot already running"; }
        feature = featureName;
        tag = label;
        twin = sameFrame;
        plan = twin ? new[] { "off", "off", "on", "on" } : new[] { "off", "off", "on", "off", "on" };
        step = 0;
        wait = SettleFrames;
        reference = null;
        log.Clear();
        log.Append(CultureInfo.InvariantCulture, $"frame shot {tag} feature={feature} {(twin ? "same frame" : "frozen")} {GameMain.GraphicsWidth}x{GameMain.GraphicsHeight}\n");
        Set(plan[0]);
        Probes.FrameDrawn += Frame;
        Sequencer.Blocking = true;
        return $"shot: {string.Join("/", plan)} for {feature}";
    }

    static void Set(string state)
    {
        foreach (Feature f in Runtime.Features) { if (f.Name == feature) { f.Enabled = state == "on"; } }
    }

    static void Frame()
    {
        if (!twin) { DebugConsole.Paused = true; }
        if (--wait > 0) { return; }
        if (!twin)
        {
            Capture();
            if (step == plan.Length) { Finish(); return; }
            Set(plan[step]);
            wait = SettleFrames;
            return;
        }
        var device = GameMain.Instance.GraphicsDevice;
        while (step < plan.Length)
        {
            Set(plan[step]);
            GameMain.GameScreen.Draw(Timing.Step, device, GameMain.spriteBatch);
            Capture();
        }
        Finish();
    }

    static void Capture()
    {
        var device = GameMain.Instance.GraphicsDevice;
        int w = device.PresentationParameters.BackBufferWidth, h = device.PresentationParameters.BackBufferHeight;
        if (current == null || current.Length != w * h) { current = new Color[w * h]; }
        device.GetBackBufferData(current);
        string name = $"{tag}_{step}_{plan[step]}";
        Save(name, current, w, h);
        if (reference == null) { reference = (Color[])current.Clone(); log.Append($"{name}: reference\n"); }
        else { log.Append(name).Append(": ").Append(Compare(reference, current)).Append('\n'); }
        step++;
    }

    static string Compare(Color[] a, Color[] b)
    {
        long differing = 0;
        int maxDelta = 0;
        for (int i = 0; i < a.Length; i++)
        {
            uint x = a[i].PackedValue, y = b[i].PackedValue;
            if (x == y) { continue; }
            differing++;
            for (int c = 0; c < 24; c += 8) { maxDelta = Math.Max(maxDelta, Math.Abs((int)((x >> c) & 255) - (int)((y >> c) & 255))); }
        }
        return string.Create(CultureInfo.InvariantCulture, $"differing pixels {differing} ({differing * 100.0 / a.Length:F4}%), max channel delta {maxDelta}");
    }

    static void Save(string name, Color[] pixels, int w, int h)
    {
        try
        {
            string dir = Path.Combine(Bench.OutDir(), "shots");
            Directory.CreateDirectory(dir);
            using var fs = File.Create(Path.Combine(dir, $"{name}_{w}x{h}.rgba"));
            var bytes = new byte[pixels.Length * 4];
            Buffer.BlockCopy(Array.ConvertAll(pixels, p => p.PackedValue), 0, bytes, 0, bytes.Length);
            fs.Write(bytes);
        }
        catch (IOException e) { log.Append($"save failed {e.Message}\n"); }
    }

    static void Finish()
    {
        Probes.FrameDrawn -= Frame;
        if (!twin) { DebugConsole.Paused = false; }
        Runtime.SetMode(Settings.Mode);
        plan = null;
        string text = log.ToString();
        try { File.WriteAllText(Path.Combine(Bench.OutDir(), "shots", $"{tag}.txt"), text); } catch (IOException) { }
        foreach (string line in text.Split('\n')) { DebugConsole.NewMessage(line, Color.LightGreen); }
        Sequencer.Blocking = false;
        Sequencer.Next();
    }
}
