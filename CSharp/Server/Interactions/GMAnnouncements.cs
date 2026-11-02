using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private void Announce(IReadMessage input, Session session, double now) {
        GMAnnouncement value = GMAnnouncement.Read(input);
        if (session.Announcements.TryGetValue(value.Id, out var cached)) { SendAnnouncementReceipt(session, cached); return; }
        string sender;
        try {
            Require(session.Client, InteractionAction.SendGMMessage);
            if ((permissions.AnnouncementFormats(session.Client) & GMAnnouncement.FormatBit(value.Format)) == 0) { throw new UnauthorizedAccessException("Нет разрешения на выбранный формат сообщения"); }
            if (session.Actions.ContainsKey(value.Id.ToString("N"))) { throw new InvalidOperationException("Запрос уже обработан; для повтора используйте новую отправку"); }
            if (now < session.NextAnnouncement) { throw new InvalidOperationException("Подождите перед следующим сообщением"); }
            if (value.Target.Length > 0 && FindAccount(value.Target) == null) { throw new InvalidOperationException("Получатель отключился"); }
            if (value.Format == GMAnnouncement.Toast && value.Target.Length > 0 && FindAccount(value.Target) is not { InGame: true, NeedsMidRoundSync: false }) { throw new InvalidOperationException("Получатель ещё не вошёл в активный раунд"); }
            if (value.Format == GMAnnouncement.Toast && GameMain.GameSession?.IsRunning != true) { throw new InvalidOperationException("Сценарная надпись доступна во время раунда"); }
            Client player = value.HasSender && value.Sender == GMMessageSender.Player ? FindAccount(value.SenderAccount) : null;
            if (value.HasSender && value.Sender == GMMessageSender.Player && player == null) { throw new InvalidOperationException("Выбранный отправитель отключился"); }
            sender = value.ResolveSender(GameMain.Server.ServerName, session.Client.Character?.DisplayName ?? session.Client.Name, player?.Character?.DisplayName ?? player?.Name);
            // Validate all recipients before sending the first intrusive window or toast.
            foreach (Client recipient in GameMain.Server.ConnectedClients) {
                if (!AnnouncementRecipient(value, recipient)) { continue; }
                if (value.Format == GMAnnouncement.Toast && (!sessions.TryGetValue(recipient.SessionId, out Session compatible) || !compatible.WorldReady)) {
                    throw new InvalidOperationException("У получателя ещё не согласован клиент Primary Package");
                }
            }
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException) {
            SendAnnouncementReceipt(session, new() { Id = value.Id, Error = error.Message, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); return;
        }
        session.NextAnnouncement = now + .5;
        int count = 0;
        foreach (Client recipient in GameMain.Server.ConnectedClients) {
            if (!AnnouncementRecipient(value, recipient)) { continue; }
            if (value.Format == GMAnnouncement.Toast) {
                ModNet.Send(InteractionProtocol.Channel, recipient.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.GMAnnouncement); value.Write(output); });
            } else {
                // A separate message instance owns each recipient's native queue sequence.
                GameMain.Server.SendDirectChatMessage(ChatMessage.Create(sender, value.NativeText(sender), (ChatMessageType)value.NativeFormat, null,
                    textColor: new Color(value.R, value.G, value.B, value.A)), recipient);
            }
            count++;
        }
        session.Actions.Add(value.Id.ToString("N"), now);
        var receipt = new GMAnnouncementReceipt { Id = value.Id, Sender = sender, Recipients = count, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        if (session.AnnouncementOrder.Count == GMMessageState.HistoryCapacity) { session.Announcements.Remove(session.AnnouncementOrder.Dequeue()); }
        session.Announcements.Add(value.Id, receipt); session.AnnouncementOrder.Enqueue(value.Id);
        SendAnnouncementReceipt(session, receipt);
        ModLog.Message("GM announcement " + session.Account + " format=" + value.Format + " target=" + (value.Target.Length == 0 ? "everyone" : value.Target));
    }
    private static bool AnnouncementRecipient(GMAnnouncement value, Client recipient) =>
        (value.Target.Length == 0 || PermissionService.Account(recipient) == value.Target) &&
        (value.Format != GMAnnouncement.Toast || recipient.InGame && !recipient.NeedsMidRoundSync);
    private static void SendAnnouncementReceipt(Session session, GMAnnouncementReceipt receipt) =>
        ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.GMAnnouncementReceipt); receipt.Write(output); });
}
