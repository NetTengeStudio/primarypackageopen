using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private void Announce(IReadMessage input, Session session, double now) {
        Require(session.Client, InteractionAction.SendGMMessage);
        GMAnnouncement value = GMAnnouncement.Read(input);
        if (session.Actions.ContainsKey(value.Id.ToString("N"))) { return; }
        if (now < session.NextAnnouncement) { throw new InvalidOperationException("Подождите перед следующим сообщением"); }
        if (value.Target.Length > 0 && FindAccount(value.Target) == null) { throw new InvalidOperationException("Получатель отключился"); }
        if (value.Format == GMAnnouncement.Toast && value.Target.Length > 0 && FindAccount(value.Target) is not { InGame: true, NeedsMidRoundSync: false }) { throw new InvalidOperationException("Получатель ещё не вошёл в активный раунд"); }
        if (value.Format == GMAnnouncement.Toast && GameMain.GameSession?.IsRunning != true) { throw new InvalidOperationException("Сценарная надпись доступна во время раунда"); }
        session.NextAnnouncement = now + .5;
        foreach (Client recipient in GameMain.Server.ConnectedClients) {
            if (value.Target.Length > 0 && PermissionService.Account(recipient) != value.Target) { continue; }
            if (value.Format == GMAnnouncement.Toast && (!recipient.InGame || recipient.NeedsMidRoundSync)) { continue; }
            if (value.Format == GMAnnouncement.Toast) {
                if (!sessions.TryGetValue(recipient.SessionId, out Session compatible) || !compatible.WorldReady) { throw new InvalidOperationException("У получателя ещё не согласован клиент Primary Package"); }
            }
        }
        foreach (Client recipient in GameMain.Server.ConnectedClients) {
            if (value.Target.Length > 0 && PermissionService.Account(recipient) != value.Target) { continue; }
            if (value.Format == GMAnnouncement.Toast && (!recipient.InGame || recipient.NeedsMidRoundSync)) { continue; }
            if (value.Format == GMAnnouncement.Toast) {
                ModNet.Send(InteractionProtocol.Channel, recipient.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.GMAnnouncement); value.Write(output); });
            } else {
                // A separate message instance owns each recipient's native queue sequence.
                GameMain.Server.SendDirectChatMessage(ChatMessage.Create("GM", value.Text, (ChatMessageType)value.Format, null,
                    textColor: new Color(value.R, value.G, value.B, value.A)), recipient);
            }
        }
        session.Actions.Add(value.Id.ToString("N"), now);
        ModLog.Message("GM announcement " + session.Account + " format=" + value.Format + " target=" + (value.Target.Length == 0 ? "everyone" : value.Target));
    }
}
