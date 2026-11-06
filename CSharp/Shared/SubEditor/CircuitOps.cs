using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// Circuit boxes in the live editor. The box keeps its components, wires and labels in the CircuitBox component and its backing
/// items in the box's containers; the engine syncs edits as entity events that only accept a character standing at the box with
/// the resources in hand. The editor has neither, so the edit travels in the editor's own pipeline instead: the server applies it
/// to its box, builds the result (new backing items under ids from the editor's id window, the engine's own resolved event) and
/// every peer replays that result through the engine's own handler. Nothing here re-implements what a node or wire does.
/// <para>
/// The engine removes a component's or wire's backing item itself, through the entity spawner (queue and network events that belong to the game),
/// or, on a client, not at all. While a circuit edit runs those two removals are skipped: the frame removes the items on every peer instead.
/// </para>
/// </summary>
public static class CircuitOps
{
    private static int depth;
#if SERVER
    [ThreadStatic] private static CircuitBox capturing;
    [ThreadStatic] private static byte[] captured;
#endif

    public static bool Active => depth > 0;

    public readonly struct CircuitScope : IDisposable
    {
        public void Dispose() => depth--;
    }

    public static CircuitScope Enter() { depth++; return default; }

    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("CircuitBoxComponent.Remove", () => harmony.Patch(
            AccessTools.Method(typeof(CircuitBoxComponent), nameof(CircuitBoxComponent.Remove)), prefix: ModuleHost.Bind(typeof(CircuitOps), nameof(SkipPrefix))));
        ModuleHost.Guard("CircuitBoxWire.Remove", () => harmony.Patch(
            AccessTools.Method(typeof(CircuitBoxWire), nameof(CircuitBoxWire.Remove)), prefix: ModuleHost.Bind(typeof(CircuitOps), nameof(SkipPrefix))));
#if SERVER
        harmony.Patch(AccessTools.Method(typeof(CircuitBox), nameof(CircuitBox.CreateServerEvent)),
            prefix: ModuleHost.Bind(typeof(CircuitOps), nameof(CaptureEvent)));
#endif
    }

    /// <summary>A removed component or wire loses its backing item: the pipeline removes those items itself on every peer, in the same frame.</summary>
    private static bool SkipPrefix() => depth == 0;

#if SERVER
    public static byte[] Capture(CircuitBox box)
    {
        CircuitBox previous = capturing;
        byte[] previousData = captured;
        capturing = box; captured = null;
        try
        {
            box.CreateInitializationEvent();
            return captured ?? throw new InvalidOperationException("Circuit box initialization was not captured");
        }
        finally { capturing = previous; captured = previousData; }
    }

    private static bool CaptureEvent(CircuitBox __instance, INetSerializableStruct data)
    {
        if (!ReferenceEquals(capturing, __instance) || data is not CircuitBoxInitializeStateFromServerEvent) { return true; }
        var message = new WriteOnlyMessage();
        message.WriteByte((byte)CircuitBoxOpcode.ServerInitialize);
        message.WriteNetSerializableStruct(data);
        captured = message.Buffer.AsSpan(0, message.LengthBytes).ToArray();
        return false;
    }
#endif

    /// <summary>The engine's own client handler applies the resolved event. The server performed the edit when it accepted it, so for the server this is a no-op.</summary>
    public static bool Apply(ushort boxItemId, ReadOnlySpan<byte> payload)
    {
        if (!CircuitPayload.TryRead(payload, out byte index, out byte[] engineEvent)) { return false; }
#if CLIENT
        if (EntityOps.Find(boxItemId) is not Item item || index >= item.Components.Count || item.Components[index] is not CircuitBox box) { return false; }
        var message = new ReadOnlyMessage(engineEvent, isCompressed: false, startPos: 0, byteLength: engineEvent.Length, sender: null);
        using (Enter())
        {
            if ((CircuitBoxOpcode)engineEvent[0] == CircuitBoxOpcode.ServerInitialize)
            {
                var wires = new ushort[box.Wires.Count];
                for (int i = 0; i < wires.Length; i++)
                {
                    CircuitBoxWire wire = box.Wires[i];
                    wires[i] = wire.ID;
                    if (wire.BackingWire.TryUnwrap(out Item backing)) { backing.GetComponent<Wire>()?.ClearConnections(); }
                }
                box.RemoveWireInternal(wires);
            }
            box.ClientEventRead(message, 0f);
        }
#endif
        return true;
    }
}
