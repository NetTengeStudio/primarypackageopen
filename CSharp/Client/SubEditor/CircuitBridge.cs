using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// The seam between the engine's circuit box screen and the live editor, same idea as <see cref="CommandBridge"/>: the original UI runs
/// unchanged, and where it would send an entity event (which the server accepts only from a character at the box) the edit goes to the
/// editor's pipeline instead. The result comes back as the server's resolved event and the engine's own handler applies it, so the box
/// changes on this client exactly when it changes on everyone else's.
/// </summary>
internal static class CircuitBridge
{
    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("CircuitBox.CreateClientEvent", () => harmony.Patch(
            AccessTools.Method(typeof(CircuitBox), nameof(CircuitBox.CreateClientEvent)), prefix: ModuleHost.Bind(typeof(CircuitBridge), nameof(CreateEventPrefix))));
        ModuleHost.Guard("CircuitBox.SendMessage", () => harmony.Patch(
            AccessTools.Method(typeof(CircuitBox), nameof(CircuitBox.SendMessage)), prefix: ModuleHost.Bind(typeof(CircuitBridge), nameof(SendMessagePrefix))));
    }

    /// <summary>The cursor relay of the box UI is addressed to characters at the box; the editor's cursors travel in <see cref="LiveView"/>.</summary>
    private static bool SendMessagePrefix() => !SubEditorClient.Editing;

    private static bool CreateEventPrefix(CircuitBox __instance, INetSerializableStruct data)
    {
        if (!SubEditorClient.Editing) { return true; }
        try
        {
            switch (data)
            {
                // What is selected in the box is each editor's own business: every editor's character has the same id, so a shared selection could not tell them apart.
                case CircuitBoxSelectNodesEvent nodes:
                    __instance.SelectComponentsInternal(nodes.TargetIDs, nodes.CharacterID, nodes.Overwrite);
                    __instance.SelectInputOutputInternal(nodes.IOs, nodes.CharacterID, nodes.Overwrite);
                    __instance.SelectLabelsInternal(nodes.LabelIDs, nodes.CharacterID, nodes.Overwrite);
                    return false;
                case CircuitBoxSelectWiresEvent wires:
                    __instance.SelectWiresInternal(wires.TargetIDs, wires.CharacterID, wires.Overwrite);
                    return false;
            }

            var message = new WriteOnlyMessage();
            message.WriteByte((byte)new CircuitBoxEventData(data).Opcode);
            message.WriteNetSerializableStruct(data);
            var w = new ByteWriter(message.LengthBytes + 8);
            try
            {
                CircuitPayload.Write(ref w, (byte)__instance.Item.GetComponentIndex(__instance), message.Buffer.AsSpan(0, message.LengthBytes));
                if (!SubEditorClient.SendRequest(OpKind.Circuit, new[] { __instance.Item.ID }, w.Written, null, description: "Circuit box")) { Say("The circuit box edit could not be sent."); }
            }
            finally { w.Dispose(); }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            ModLog.Once($"subeditor: circuit box edit could not be sent: {e.Message}");
            Say("The circuit box edit could not be sent.");
        }
        return false;
    }

    private static void Say(string text) => global::Barotrauma.GUI.AddMessage(text, Microsoft.Xna.Framework.Color.Orange);
}
