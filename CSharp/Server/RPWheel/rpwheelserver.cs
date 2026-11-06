using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.LuaCs.Events;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;
using PrimaryPackage.Emotions;

namespace PrimaryPackage.RPWheel;

public sealed class RPWheelServer : IModule, IEventUpdate, IEventClientDisconnected, IEventRoundEnded {
    private sealed class Run {
        internal Character Actor;
        internal Emotions.EmoteDef Definition;
        internal byte Variant;
        internal ulong Sequence;
        internal double Start, End;
        internal readonly List<NetworkConnection> Observers = new();
    }
    private sealed class Session {
        internal Client Client;
        internal Character Actor;
        internal readonly double[] Last = new double[Emotions.EmoteCatalog.MaxEmotes];
        internal Session(Client client, Character actor) { Client = client; Actor = actor; Array.Fill(Last, double.NegativeInfinity); }
    }
    private readonly Dictionary<Character, Run> active = new();
    private readonly Dictionary<byte, Session> sessions = new();
    private readonly List<Character> done = new();
    private IDisposable receiver;
    private ulong sequence;
    public void Attach(Harmony harmony) {
        EmoteCatalog.Load(); receiver = INet.Net.Listen(Cfg.NetId, Receive, .15);
        LuaCsSetup.Instance.EventService.Subscribe<IEventUpdate>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventClientDisconnected>(this);
        LuaCsSetup.Instance.EventService.Subscribe<IEventRoundEnded>(this);
    }
    public void RoundStart() { foreach (var run in active.Values) { SendStop(run); } active.Clear(); sessions.Clear(); done.Clear(); }
    public void OnRoundEnd() => RoundStart();
    public void Release() {
        RoundStart(); receiver?.Dispose(); receiver = null;
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventClientDisconnected>(this);
        LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventRoundEnded>(this);
    }
    public void OnClientDisconnected(Client client) {
        if (!sessions.Remove(client.SessionId, out var session)) { return; }
        if (session.Actor != null && active.Remove(session.Actor, out var run)) { SendStop(run); }
    }
    private void Receive(IReadMessage input, Client client) {
        if (input.LengthBytes > 256 || client?.Connection == null) { return; }
        var header = (NetHeader)input.ReadRangedInteger(Cfg.HeaderMin, Cfg.HeaderMax);
        if (header == NetHeader.Hello) {
            INet.Net.Send(Cfg.NetId, client.Connection, output => { output.WriteRangedInteger((int)NetHeader.Hello, Cfg.HeaderMin, Cfg.HeaderMax); output.WriteString(EmoteCatalog.Fingerprint); });
            foreach (var run in active.Values) {
                if (CanHear(client, run)) { SendStart(client.Connection, run); if (!run.Observers.Contains(client.Connection)) { run.Observers.Add(client.Connection); } }
            }
            return;
        }
        if (header != NetHeader.EmoteRequest) { return; }
        byte index = input.ReadByte(); string fingerprint = input.ReadString();
        if (fingerprint != EmoteCatalog.Fingerprint || index >= EmoteCatalog.Count) { Deny(client, "Каталоги эмоций клиента и сервера различаются"); return; }
        Character actor = client.Character;
        if (actor == null) { return; }
        if (!sessions.TryGetValue(client.SessionId, out var session) || session.Client != client || session.Actor != actor) { sessions[client.SessionId] = session = new Session(client, actor); }
        var definition = EmoteCatalog.Source[index];
        var facts = EmotionFacts.Read(actor, active.ContainsKey(actor), Timing.TotalTime - session.Last[index]);
        EmoteDeny denied = EmoteRules.Check(definition, facts);
        if (denied != EmoteDeny.None) { Deny(client, EmoteRules.Explain(denied)); return; }
        byte variant = (byte)EmoteRules.Variant(definition, EmotionFacts.Gender(actor), (uint)Rand.Int(int.MaxValue, Rand.RandSync.Unsynced));
        double now = Timing.TotalTime;
        var current = new Run { Actor = actor, Definition = definition, Variant = variant, Sequence = ++sequence, Start = now, End = now + Poses.Total(definition.Pose, definition.Duration(variant)) };
        active.Add(actor, current); session.Last[index] = now;
        foreach (Client observer in GameMain.Server.ConnectedClients) {
            if (!CanHear(observer, current)) { continue; }
            current.Observers.Add(observer.Connection); SendStart(observer.Connection, current);
        }
        Interactions.InteractionServer.RecordEmote(client, definition.Bubble);
    }
    private static bool CanHear(Client client, Run run) => client.Character == null || client.Character == run.Actor || Vector2.DistanceSquared(client.Character.WorldPosition, run.Actor.WorldPosition) <= Math.Max(run.Definition.Range, ChatMessage.SpeakRange) * Math.Max(run.Definition.Range, ChatMessage.SpeakRange);
    private static void Deny(Client client, string text) => INet.Net.Send(Cfg.NetId, client.Connection, output => { output.WriteRangedInteger((int)NetHeader.Denied, Cfg.HeaderMin, Cfg.HeaderMax); output.WriteString(text); });
    private static void SendStart(NetworkConnection connection, Run run) => INet.Net.Send(Cfg.NetId, connection, output => {
        output.WriteRangedInteger((int)NetHeader.EmoteBroadcast, Cfg.HeaderMin, Cfg.HeaderMax); output.WriteUInt16(run.Actor.ID); output.WriteUInt64(run.Sequence);
        output.WriteByte(run.Definition.Index); output.WriteByte(run.Variant); output.WriteSingle((float)Math.Max(0, Timing.TotalTime - run.Start));
    });
    private static void SendStop(Run run) {
        foreach (var connection in run.Observers) { INet.Net.Send(Cfg.NetId, connection, output => { output.WriteRangedInteger((int)NetHeader.EmoteStop, Cfg.HeaderMin, Cfg.HeaderMax); output.WriteUInt16(run.Actor.ID); output.WriteUInt64(run.Sequence); }); }
    }
    public void OnUpdate(double delta) {
        if (active.Count == 0) { return; }
        done.Clear();
        foreach (var run in active.Values) { if (Timing.TotalTime >= run.End || EmoteRules.Interrupted(run.Definition, EmotionFacts.Read(run.Actor, false, double.MaxValue))) { done.Add(run.Actor); } }
        foreach (Character actor in done) { if (active.Remove(actor, out var run)) { SendStop(run); } }
    }
}
