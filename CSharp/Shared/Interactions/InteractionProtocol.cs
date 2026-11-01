using System;
using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

public enum InteractionAction : byte
{
    ViewCharacter, SendPrivateMessage, QueryCharacters, QueryItems, SpawnCharacter, ControlCharacter, FlickerLight, ManagePermissions,
    ViewGMFeed, ManageEntities, DirectEvents, ManageKits
}
public enum MessageKind : byte { Private, Support, Roleplay }
public enum EntityOperation : byte { Condition, RemoveItem, Gib, BreakDoor, SpawnCreature, SpawnDormantCreature, AIEnabled }
public enum DirectorKind : byte { Sound, Scare, Caption, Stop, StopOne }
public enum MessageStatus : byte { Queued, Stored, Delivered, Read }
internal enum InteractionPacket : byte { Hello, Query, Spawn, Flicker, PermissionList, PermissionChange, MessageSend, MessageAck, MessageRead, Snapshot, QueryResult, Message, MessageState, PermissionResult, Error, ChannelSend, EntityAction, Director, DirectorEvent, HistoryEnd, DirectorBatch, DirectorList, DirectorStatus, ReceiptBatch }

internal static class InteractionProtocol
{
    public const string Channel = "pp.interactions.v2";
    public const int Version = 2, MaxMessageLength = 512, MaxQueryResults = 200, MaxMessages = 10000;
    public static void Header(IWriteMessage message, InteractionPacket packet) { message.WriteByte(Version); message.WriteByte((byte)packet); }
    public static bool Read(IReadMessage message, out InteractionPacket packet)
    {
        packet = default;
        if (message.LengthBits - message.BitPosition < 16) { return false; }
        if (message.ReadByte() != Version) { return false; }
        byte value = message.ReadByte();
        if (value > (byte)InteractionPacket.ReceiptBatch) { return false; }
        packet = (InteractionPacket)value;
        return true;
    }
    public static bool ValidText(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) { return false; }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsControl(c) || c is '<' or '>') { return false; }
            if (char.IsHighSurrogate(c)) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) { return false; } }
            else if (char.IsLowSurrogate(c)) { return false; }
        }
        return true;
    }
}

public sealed class PrivateMessage
{
    public MessageKind Kind { get; set; }
    public string Thread { get; set; } = "";
    public long Sequence { get; set; }
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public string Text { get; set; } = "";
    public long Timestamp { get; set; }
    public MessageStatus Status { get; set; }
}

public static class MessageAccess {
    public static bool CanView(PrivateMessage message, string account, bool staff) => message.Kind switch {
        MessageKind.Private => message.From == account || message.To == account,
        MessageKind.Support => message.Thread == account || staff,
        MessageKind.Roleplay => staff,
        _ => false
    };
}

public readonly struct PermissionAuditEntry
{
    public readonly string Actor;
    public readonly InteractionAction Action;
    public readonly byte Mode;
    public readonly long Timestamp;
    public PermissionAuditEntry(string actor, InteractionAction action, byte mode, long timestamp) { Actor = actor; Action = action; Mode = mode; Timestamp = timestamp; }
}
