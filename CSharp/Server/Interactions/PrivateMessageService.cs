using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PrimaryPackage.Interactions;

public sealed class PrivateMessageService : IDisposable {
    private readonly Dictionary<string, PrivateMessage> messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PrivateMessage>> mailboxes = new(StringComparer.Ordinal);
    private readonly List<PrivateMessage> staff = new();
    private readonly Dictionary<string, List<PrivateMessage>> support = new(StringComparer.Ordinal);
    private readonly FileStream journal;
    private readonly FileStream replica;
    private long sequence;
    private bool faulted;
    public int Count => messages.Count;
    public PrivateMessageService(string directory) {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "messages.v1.journal");
        journal = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try {
            replica = new FileStream(path + ".replica", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try { Recover(journal); }
            catch (Exception error) when (error is InvalidDataException or JsonException) {
                ResetIndex();
                if (replica.Length == 0) { throw; }
                Recover(replica);
                File.Copy(path, path + ".corrupt." + Guid.NewGuid().ToString("N"));
                Copy(replica, journal);
            }
            if (replica.Length > journal.Length) {
                long primaryLength = journal.Length;
                ResetIndex();
                try {
                    Recover(replica);
                    if (replica.Length >= primaryLength) { Copy(replica, journal); }
                    else { ResetIndex(); Recover(journal); }
                } catch (Exception error) when (error is InvalidDataException or JsonException) {
                    File.Copy(path + ".replica", path + ".replica.corrupt." + Guid.NewGuid().ToString("N"));
                    ResetIndex();
                    Recover(journal);
                }
            }
            Copy(journal, replica);
        } catch { replica?.Dispose(); journal.Dispose(); throw; }
    }
    private void ResetIndex() { messages.Clear(); mailboxes.Clear(); staff.Clear(); support.Clear(); sequence = 0; }
    private static void Copy(FileStream source, FileStream destination) {
        source.Position = 0;
        destination.Position = 0;
        destination.SetLength(0);
        source.CopyTo(destination);
        destination.Flush(true);
        source.Position = source.Length;
        destination.Position = destination.Length;
    }
    private void Recover(FileStream source) {
        source.Position = 0;
        if (source.Length > 128 * 1024 * 1024) { throw new InvalidDataException("Message journal exceeds capacity"); }
        Span<byte> header = stackalloc byte[36];
        Span<byte> hash = stackalloc byte[32];
        long validLength = 0;
        while (source.Position < source.Length) {
            if (source.Length - source.Position < header.Length) { break; }
            source.ReadExactly(header);
            int size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size < 2 || size > 32768) { throw new InvalidDataException("Invalid message record length"); }
            if (source.Length - source.Position < size) { break; }
            byte[] payload = new byte[size];
            source.ReadExactly(payload);
            SHA256.HashData(payload, hash);
            if (!CryptographicOperations.FixedTimeEquals(hash, header.Slice(4))) { throw new InvalidDataException("Message journal checksum failure"); }
            PrivateMessage message = JsonSerializer.Deserialize<PrivateMessage>(payload);
            if (!Valid(message)) { throw new InvalidDataException("Invalid message journal record"); }
            Apply(message);
            validLength = source.Position;
        }
        if (source.Length != validLength) { source.SetLength(validLength); source.Flush(true); }
        source.Position = validLength;
    }
    private static bool Valid(PrivateMessage message) => message != null && message.Sequence > 0 &&
        Guid.TryParseExact(message.Id, "N", out _) && PermissionService.ValidAccount(message.From) && PermissionService.ValidAccount(message.To) &&
        InteractionProtocol.ValidText(message.Text, InteractionProtocol.MaxMessageLength) && Enum.IsDefined(message.Kind) &&
        (message.Kind == MessageKind.Private || PermissionService.ValidAccount(message.Thread)) &&
        message.SenderName != null && message.SenderName.Length <= 128 && message.RecipientName != null && message.RecipientName.Length <= 128 && message.Status is >= MessageStatus.Stored and <= MessageStatus.Read;
    private static string Key(string sender, string id) => sender + ":" + id;
    public PrivateMessage Store(string sender, string recipient, string senderName, string recipientName, string id, string text) {
        return StoreInChannel(sender, recipient, senderName, recipientName, id, text, MessageKind.Private, "");
    }
    public PrivateMessage StoreInChannel(string sender, string recipient, string senderName, string recipientName, string id, string text, MessageKind kind, string thread) {
        if (!Guid.TryParseExact(id, "N", out _) || !PermissionService.ValidAccount(sender) || !PermissionService.ValidAccount(recipient) || sender == recipient ||
            !InteractionProtocol.ValidText(text, InteractionProtocol.MaxMessageLength) || senderName == null || senderName.Length > 128 || recipientName == null || recipientName.Length > 128) { throw new ArgumentException("Invalid private message"); }
        if (!Enum.IsDefined(kind) || kind != MessageKind.Private && !PermissionService.ValidAccount(thread)) { throw new ArgumentException("Invalid message channel"); }
        if (messages.TryGetValue(Key(sender, id), out var existing)) {
            if (existing.To != recipient || existing.Text != text || existing.Kind != kind || existing.Thread != thread) { throw new InvalidDataException("Message id reused with different content"); }
            return existing;
        }
        if (messages.Count >= InteractionProtocol.MaxMessages || kind == MessageKind.Private && (Mailbox(sender).Count >= 1000 || Mailbox(recipient).Count >= 1000) ||
            kind == MessageKind.Support && support.TryGetValue(thread, out var conversation) && conversation.Count >= 1000) { throw new InvalidOperationException("Message storage capacity reached"); }
        var message = new PrivateMessage {
            Sequence = checked(sequence + 1), Id = id, From = sender, To = recipient,
            SenderName = senderName, RecipientName = recipientName, Text = text,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Status = MessageStatus.Stored, Kind = kind, Thread = thread
        };
        Append(message);
        Apply(message);
        return message;
    }
    public PrivateMessage Acknowledge(string recipient, string sender, string id, bool read) {
        if (!messages.TryGetValue(Key(sender, id), out var existing) || existing.Kind != MessageKind.Private || existing.To != recipient) { throw new UnauthorizedAccessException("Message acknowledgement denied"); }
        MessageStatus status = read ? MessageStatus.Read : MessageStatus.Delivered;
        if (status <= existing.Status) { return existing; }
        var next = new PrivateMessage { Sequence = existing.Sequence, Id = existing.Id, From = existing.From, To = existing.To,
            SenderName = existing.SenderName, RecipientName = existing.RecipientName, Text = existing.Text, Timestamp = existing.Timestamp, Status = status, Kind = existing.Kind, Thread = existing.Thread };
        Append(next);
        existing.Status = status;
        return existing;
    }
    public IReadOnlyList<PrivateMessage> History(string account) => Mailbox(account);
    public IReadOnlyList<PrivateMessage> StaffHistory => staff;
    public IReadOnlyList<PrivateMessage> SupportHistory(string account) => support.TryGetValue(account, out var values) ? values : Array.Empty<PrivateMessage>();
    private List<PrivateMessage> Mailbox(string account) {
        if (!mailboxes.TryGetValue(account, out var mailbox)) { mailbox = new List<PrivateMessage>(); mailboxes.Add(account, mailbox); }
        return mailbox;
    }
    private void Append(PrivateMessage message) {
        if (faulted) { throw new IOException("Message journal requires recovery"); }
        if (journal.Length >= 128 * 1024 * 1024 - 32768) { throw new IOException("Message journal capacity reached"); }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        Span<byte> header = stackalloc byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        SHA256.HashData(payload, header.Slice(4));
        try {
            journal.Write(header); journal.Write(payload); journal.Flush(true);
            replica.Write(header); replica.Write(payload); replica.Flush(true);
        }
        catch { faulted = true; throw; }
    }
    private void Apply(PrivateMessage message) {
        string key = Key(message.From, message.Id);
        if (messages.TryGetValue(key, out var existing)) {
            if (existing.Sequence != message.Sequence || existing.To != message.To || existing.Text != message.Text || existing.Kind != message.Kind || existing.Thread != message.Thread || message.Status < existing.Status) { throw new InvalidDataException("Message journal invariant failure"); }
            existing.Status = message.Status;
        } else {
            if (message.Sequence != sequence + 1 || messages.Count >= InteractionProtocol.MaxMessages) { throw new InvalidDataException("Message sequence invariant failure"); }
            messages.Add(key, message);
            if (message.Kind == MessageKind.Private) {
                Mailbox(message.From).Add(message);
                Mailbox(message.To).Add(message);
            } else {
                staff.Add(message);
                if (message.Kind == MessageKind.Support) {
                    if (!support.TryGetValue(message.Thread, out var thread)) { thread = new List<PrivateMessage>(); support.Add(message.Thread, thread); }
                    thread.Add(message);
                }
            }
            sequence = message.Sequence;
        }
    }
    public void Dispose() { try { journal.Dispose(); } finally { replica.Dispose(); } }
}
