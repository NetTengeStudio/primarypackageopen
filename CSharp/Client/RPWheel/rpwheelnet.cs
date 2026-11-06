using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using PrimaryPackage.Core;
using PrimaryPackage.Emotions;

namespace PrimaryPackage.RPWheel;

internal static class RPWheelNet {
    private static readonly Dictionary<ushort, (Character Actor, ulong Sequence)> runs = new();
    private static NetworkConnection connection;
    private static double nextHello;
    private static bool ready;
    private static IDisposable receiver;
    internal static bool IsMultiplayer => GameMain.Client != null;
    internal static void Register() { receiver?.Dispose(); receiver = INet.Net.Listen(Cfg.NetId, Receive); }
    internal static void Reset() { runs.Clear(); connection = null; ready = false; nextHello = 0; }
    internal static void Release() { receiver?.Dispose(); receiver = null; Reset(); }
    internal static void Update() {
        var current = GameMain.Client?.ClientPeer?.ServerConnection;
        if (current != connection) { Reset(); EmotePlayback.Reset(); connection = current; }
        if (!PlayView.Running || current == null || ready || Timing.TotalTime < nextHello) { return; }
        nextHello = Timing.TotalTime + 3;
        INet.Net.Send(Cfg.NetId, output => output.WriteRangedInteger((int)NetHeader.Hello, Cfg.HeaderMin, Cfg.HeaderMax));
    }
    internal static void Note(string text) {
        var window = PrimaryPackage.GUI.GUIRuntime.Instance?.Windows.Notify("Эмоция", new Microsoft.Xna.Framework.Vector2(30, 80), 4);
        window?.Add(new PrimaryPackage.GUI.Text(text) { Wrap = true, Height = 60 });
    }
    private static void Receive(IReadMessage input) {
        if (input.LengthBytes > 256) { return; }
        switch ((NetHeader)input.ReadRangedInteger(Cfg.HeaderMin, Cfg.HeaderMax)) {
            case NetHeader.Hello:
                ready = input.ReadString() == EmoteCatalog.Fingerprint;
                if (!ready) { Note("Каталог эмоций отличается от сервера"); }
                break;
            case NetHeader.Denied: Note(input.ReadString()); break;
            case NetHeader.EmoteBroadcast: {
                ushort id = input.ReadUInt16(); ulong sequence = input.ReadUInt64(); byte index = input.ReadByte(), variant = input.ReadByte(); float age = input.ReadSingle();
                if (!ready || Entity.FindEntityByID(id) is not Character actor || EmoteCatalog.Get(index) == null) { return; }
                if (runs.TryGetValue(id, out var previous) && previous.Sequence >= sequence) { return; }
                runs[id] = (actor, sequence); EmotePlayback.Start(actor, index, variant, age); break;
            }
            case NetHeader.EmoteStop: {
                ushort id = input.ReadUInt16(); ulong sequence = input.ReadUInt64();
                if (runs.TryGetValue(id, out var run) && run.Sequence == sequence) { EmotePlayback.Stop(run.Actor); runs[id] = (null, sequence); } break;
            }
        }
    }
}

internal static class EmoteNet {
    private static readonly Dictionary<byte, double> last = new();
    internal static void Reset() => last.Clear();
    internal static void Send(byte index) {
        Character actor = Character.Controlled; var definition = EmoteCatalog.Source?[index];
        if (actor == null || definition == null) { return; }
        if (RPWheelNet.IsMultiplayer) {
            INet.Net.Send(Cfg.NetId, output => { output.WriteRangedInteger((int)NetHeader.EmoteRequest, Cfg.HeaderMin, Cfg.HeaderMax); output.WriteByte(index); output.WriteString(EmoteCatalog.Fingerprint); }); return;
        }
        double now = Timing.TotalTime;
        EmoteDeny denied = EmoteRules.Check(definition, EmotionFacts.Read(actor, EmotePlayback.Busy(actor), last.TryGetValue(index, out double previous) ? now - previous : double.MaxValue));
        if (denied != EmoteDeny.None) { RPWheelNet.Note(EmoteRules.Explain(denied)); return; }
        last[index] = now;
        byte variant = (byte)EmoteRules.Variant(definition, EmotionFacts.Gender(actor), (uint)Rand.Int(int.MaxValue, Rand.RandSync.Unsynced));
        EmotePlayback.Start(actor, index, variant, 0);
    }
}
