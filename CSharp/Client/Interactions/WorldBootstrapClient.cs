using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;
using PrimaryPackage.Networking;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    private IDisposable worldReceiver;
    private readonly byte[] worldPacket = new byte[ChunkWire.MaxPacketBytes];
    private Guid bootstrapFlow, bootstrapEpoch;
    private ChunkReceiver bootstrapReceiver;
    private Task<WorldBaseline> bootstrapParsing;
    private ulong bootstrapCut;
    private int bootstrapPart = -1;
    private bool bootstrapApplied, bootstrapReleased, bootstrapFailed, bootstrapNativeReady;
    private double bootstrapRequestAt, bootstrapAckAt, bootstrapProgressAt;
    private PrimaryPackage.GUI.GUIWindow bootstrapWindow;
    private PrimaryPackage.GUI.Text bootstrapText;
    private int bootstrapShownProgress = -1;
    internal static bool WorldSynchronizing => Instance is { bootstrapReleased: false, bootstrapNativeReady: true } client && client.Multiplayer;
    private void AttachWorldBootstrap(Harmony harmony) {
        worldReceiver = ModNet.Listen(WorldContract.Channel, ReadWorldPacket);
        harmony.Patch(AccessTools.Method(typeof(GameClient), "SendIngameUpdate"), prefix: ModuleHost.Bind(typeof(InteractionClient), nameof(SendNativeGate)));
        harmony.Patch(AccessTools.Method(typeof(GameClient), "ReadIngameUpdate"), prefix: ModuleHost.Bind(typeof(InteractionClient), nameof(ReadNativeGate)));
        harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update)), prefix: ModuleHost.Bind(typeof(InteractionClient), nameof(SimulateNativeGate)));
        harmony.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(GameClient), "StartGame")), transpiler: ModuleHost.Bind(typeof(InteractionClient), nameof(FinalizeNativeWorld)));
    }
    private static bool SendNativeGate() => Instance == null || Instance.bootstrapReleased;
    private static bool ReadNativeGate() => Instance == null || Instance.bootstrapReleased;
    private static bool SimulateNativeGate() => !WorldSynchronizing;
    private void ResetWorldBootstrap() {
        bootstrapReceiver?.Dispose(); bootstrapReceiver = null; bootstrapParsing = null;
        bootstrapFlow = bootstrapEpoch = Guid.Empty; bootstrapPart = -1; bootstrapCut = 0;
        bootstrapApplied = bootstrapReleased = bootstrapFailed = bootstrapNativeReady = false; bootstrapRequestAt = bootstrapAckAt = 0; bootstrapShownProgress = -1;
        bootstrapWindow?.Dispose(); bootstrapWindow = null; bootstrapText = null; NativeWorld.Prune();
    }
    private void PumpWorldBootstrap(double now) {
        if (!bootstrapNativeReady || !GameMain.Client.GameStarted || bootstrapFailed || bootstrapReleased) { return; }
        if (bootstrapFlow == Guid.Empty) { bootstrapFlow = Guid.NewGuid(); bootstrapProgressAt = now; }
        if (bootstrapReceiver == null) {
            if (now >= bootstrapRequestAt) { bootstrapRequestAt = now + 1; SendBootstrapAccept(bootstrapFlow); }
            if (now - bootstrapProgressAt >= 60) { FailBootstrap("Сервер не начал передачу структуры мира за 60 секунд"); }
        } else {
            if (bootstrapReceiver.Expired(now)) { FailBootstrap("Передача структуры мира не продвигается 60 секунд"); return; }
            if (now >= bootstrapAckAt) {
                bootstrapAckAt = now + .1;
                var writer = ChunkWire.Start(worldPacket, ChunkPacket.Ack, bootstrapReceiver.Id); writer.UInt((uint)bootstrapReceiver.Prefix); writer.Bits(bootstrapReceiver.Following, 64);
                SendWorldPacket(ChunkWire.Finish(ref writer, worldPacket));
            }
            if (bootstrapReceiver.Complete && bootstrapParsing == null && !bootstrapApplied) {
                byte[] bytes = bootstrapReceiver.Payload.ToArray();
                bootstrapParsing = Task.Run(() => { try { return WorldBaseline.Decode(bytes); } finally { Array.Clear(bytes, 0, bytes.Length); } });
            }
            if (bootstrapParsing is { IsCompleted: true }) {
                if (bootstrapParsing.IsFaulted) { FailBootstrap(bootstrapParsing.Exception?.GetBaseException().Message ?? "Invalid snapshot"); return; }
                try {
                    WorldBaseline snapshot = bootstrapParsing.GetAwaiter().GetResult(); bootstrapParsing = null;
                    ApplyBaseline(snapshot); bootstrapApplied = true; bootstrapRequestAt = double.NegativeInfinity;
                } catch (Exception error) { FailBootstrap(error.Message); return; }
            }
            if (bootstrapApplied && now >= bootstrapRequestAt) {
                bootstrapRequestAt = now + 1;
                var writer = ChunkWire.Start(worldPacket, ChunkPacket.Complete, bootstrapReceiver.Id); writer.Guid(bootstrapFlow); writer.UInt((uint)bootstrapPart); writer.UInt(bootstrapCut);
                SendWorldPacket(ChunkWire.Finish(ref writer, worldPacket));
            }
        }
        ShowBootstrapProgress();
    }
    private static IEnumerable<CodeInstruction> FinalizeNativeWorld(IEnumerable<CodeInstruction> instructions) {
        var select = AccessTools.Method(typeof(Screen), nameof(Screen.Select));
        var gameScreen = AccessTools.Field(typeof(GameMain), nameof(GameMain.GameScreen));
        var marker = AccessTools.Method(typeof(InteractionClient), nameof(NativeRoundReady));
        var result = new List<CodeInstruction>(); int matches = 0; CodeInstruction previous = null;
        foreach (var instruction in instructions) {
            result.Add(instruction);
            if (instruction.Calls(select) && previous?.LoadsField(gameScreen) == true) { result.Add(new CodeInstruction(OpCodes.Call, marker)); matches++; }
            if (instruction.opcode != OpCodes.Nop) { previous = instruction; }
        }
        if (matches != 1) { throw new InvalidOperationException("Unsupported native round-finalize lifecycle: expected one GameScreen.Select, found " + matches); }
        return result;
    }
    private static void NativeRoundReady() { if (Instance != null) { Instance.bootstrapNativeReady = true; } }
    private void SendBootstrapAccept(Guid transfer) {
        var writer = ChunkWire.Start(worldPacket, ChunkPacket.Accept, transfer); writer.Guid(bootstrapFlow); writer.UInt(WorldContract.SchemaVersion); writer.UInt(32768);
        SendWorldPacket(ChunkWire.Finish(ref writer, worldPacket));
    }
    private void ReadWorldPacket(IReadMessage message) {
        if (!ready || bootstrapFailed || message.LengthBytes > WorldContract.MaxEnvelopeBytes) { return; }
        try {
            if (!ChunkWire.TryOpen(NativeWire.Read(message), out var reader, out var kind, out Guid transfer)) { return; }
            double now = PrimaryPackage.GUI.GUIRuntime.Now;
            if (kind == ChunkPacket.Offer) {
                Guid flow = reader.Guid(); uint part = reader.UInt32(); ulong cut = reader.UInt(); uint length = reader.UInt32(); ReadOnlySpan<byte> hash = reader.Bytes(32); reader.Finish();
                if (flow != bootstrapFlow || bootstrapReleased) { return; }
                if (part > 4096 || length > WorldBaseline.MaxEncodedBytes) { throw new ArgumentException("Invalid snapshot segment"); }
                if (bootstrapReceiver?.Id == transfer) { SendBootstrapAccept(transfer); return; }
                if (part != bootstrapPart + 1 || bootstrapReceiver != null && !bootstrapApplied || cut < bootstrapCut) { return; }
                // Parsing has completed before replacing the borrowed receive buffer.
                bootstrapReceiver?.Dispose(); bootstrapReceiver = new ChunkReceiver(transfer, (int)length, hash, now);
                bootstrapPart = (int)part; bootstrapCut = cut; bootstrapApplied = false; bootstrapParsing = null;
                bootstrapProgressAt = now; bootstrapShownProgress = -1; SendBootstrapAccept(transfer);
            } else if (bootstrapReceiver?.Id == transfer && kind == ChunkPacket.Data) {
                uint index = reader.UInt32(); if (index >= bootstrapReceiver.ChunkCount) { throw new ArgumentException("Invalid snapshot chunk index"); }
                int length = Math.Min(WorldBlob.ChunkBytes, bootstrapReceiver.Length - (int)index * WorldBlob.ChunkBytes);
                var bytes = reader.Bytes(length); reader.Finish(); bootstrapReceiver.Receive((int)index, bytes, now);
            }
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { FailBootstrap(error.Message); }
    }
    private void ReleaseWorldBootstrap(IReadMessage message) {
        Guid flow = Guid.ParseExact(message.ReadString(), "N"), epoch = Guid.ParseExact(message.ReadString(), "N"); ulong head = message.ReadUInt64();
        if (flow != bootstrapFlow || epoch != bootstrapEpoch || head < bootstrapCut || !bootstrapApplied) { FailBootstrap("Invalid ordered world handoff"); return; }
        if (bootstrapReleased) { return; }
        bootstrapReleased = true; bootstrapReceiver?.Dispose(); bootstrapReceiver = null;
        bootstrapWindow?.Dispose(); bootstrapWindow = null; bootstrapText = null;
    }
    private void ApplyBaseline(WorldBaseline snapshot) {
        if (snapshot.Cut != bootstrapCut || bootstrapEpoch != Guid.Empty && snapshot.Epoch != bootstrapEpoch) { throw new InvalidOperationException("World epoch/revision changed during transfer"); }
        NativeWorld.Validate(snapshot.Graphs);
        var mutations = new List<WorldMutation>(); var targets = new List<Item>(); var bindings = new List<(BaselineItem State, Item Item)>();
        foreach (var state in snapshot.Items) {
            if (Entity.FindEntityByID(state.NativeId) is not Item { Removed: false, FullyInitialized: true } item || item.Prefab.Identifier.Value != state.Prefab || (item.Submarine?.ID ?? 0) != state.Submarine) { throw new InvalidOperationException("Не совпала базовая сущность ID " + state.NativeId + " (" + state.Prefab + ")"); }
            if (deviceBindings.TryGetValue(state.Incarnation, out var previous) && previous != item) { throw new InvalidOperationException("Baseline incarnation was replaced"); }
            bindings.Add((state, item));
            if (state.HasPosition) {
                string after = LiveItemTransform.Encode(state.Frame, new Point(state.X, state.Y));
                string before = LiveItemTransform.Encode(state.Frame, new Point(item.Rect.X, item.Rect.Y));
                mutations.Add(new(new(state.Incarnation, ushort.MaxValue, LiveItemTransform.PositionKey), 0, before, after)); targets.Add(item);
            }
            foreach (var property in state.Properties) {
                var component = DeviceEditor.Resolve(item, property.Component, property.Type); var field = DeviceEditor.Resolve(component, property.Name);
                mutations.Add(new(new(state.Incarnation, property.Component, property.Type + "|" + property.Name), 0, field.Read(component), property.Value)); targets.Add(item);
            }
        }
        var array = mutations.ToArray(); var projection = new ClientWorldProjection(targets.ToArray(), array);
        foreach (var mutation in array) { projection.Validate(mutation.Key, mutation.After); }
        WorldEditJournal.Execute(array, true, projection);
        foreach (var binding in bindings) {
            var state = binding.State; Item item = binding.Item; deviceBindings[state.Incarnation] = item;
            if (state.HasPosition) { transformStates[item] = new(state.Incarnation, state.PositionRevision, LiveItemTransform.Encode(state.Frame, new Point(state.X, state.Y)), "", item.Submarine); }
        }
        bootstrapEpoch = snapshot.Epoch;
    }
    private bool SendWorldPacket(int length) => ModNet.Send(WorldContract.Channel, output => NativeWire.Write(output, worldPacket, length), DeliveryMethod.Unreliable);
    private void FailBootstrap(string reason) {
        bootstrapFailed = true;
        if (bootstrapReceiver != null) {
            var writer = ChunkWire.Start(worldPacket, ChunkPacket.Cancel, bootstrapReceiver.Id); writer.Text(reason.Length > 256 ? reason[..256] : reason, 1024);
            int length = ChunkWire.Finish(ref writer, worldPacket);
            ModNet.Send(WorldContract.Channel, output => NativeWire.Write(output, worldPacket, length), DeliveryMethod.Reliable);
        }
        ModLog.Error("World bootstrap: " + reason);
        bootstrapWindow?.Dispose(); bootstrapWindow = null; bootstrapText = null;
        views.Error("Синхронизация мира остановлена: " + reason);
    }
    private void ShowBootstrapProgress() {
        int progress = bootstrapReceiver == null ? 0 : bootstrapReceiver.ReceivedChunks * 100 / bootstrapReceiver.ChunkCount;
        if (progress == bootstrapShownProgress && bootstrapWindow is { Disposed: false }) { return; } bootstrapShownProgress = progress;
        if (bootstrapWindow is not { Disposed: false }) {
            var runtime = PrimaryPackage.GUI.GUIRuntime.Instance;
            bootstrapWindow = runtime.Windows.Open("Синхронизация структуры мира", new Vector2(20, 20), 480, 100);
            bootstrapText = bootstrapWindow.Add(new PrimaryPackage.GUI.Text("") { Wrap = true, Height = 64 }); bootstrapWindow.InputTransparent = true;
        }
        bootstrapText.Text = bootstrapReceiver == null ? "Ожидание снимка от сервера…" : "Получено " + progress + "% · часть " + (bootstrapPart + 1) + (bootstrapApplied ? " · проверка актуальности" : " · восстановление до игровых событий");
    }
}
