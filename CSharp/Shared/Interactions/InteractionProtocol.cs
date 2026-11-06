using System;

namespace PrimaryPackage.Interactions;

// Numeric IDs are retained for existing permissions.json files and client journals.
public enum InteractionAction : byte { SendPrivateMessage = 1, FlickerLight = 6, ViewGMFeed = 8, SendGMMessage = 12 }
public enum MessageKind : byte { Private, Support, Roleplay }
public enum MessageStatus : byte { Queued, Stored, Delivered, Read }
internal enum InteractionPacket : byte {
    Hello, MessageSend, MessageAck, MessageRead, Snapshot, Message, Error,
    ChannelSend, HistoryEnd, ReceiptBatch, GMAnnouncement, GMAnnouncementReceipt, Flicker
}

internal static partial class InteractionProtocol {
    public const string Channel = "pp.interactions.v5";
    public const int Version = 5, MaxMessageLength = 512, MaxMessages = 10000;
    public static bool ValidText(string value, int max) => InteractionText.Valid(value, max);
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
