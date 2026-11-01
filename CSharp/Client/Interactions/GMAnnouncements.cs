using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    internal void Announce(GMAnnouncement value, bool preview) {
        value.Validate();
        if (!Allows(InteractionAction.SendGMMessage)) { throw new UnauthorizedAccessException("Нет разрешения на GM-сообщения"); }
        if (preview || !Multiplayer) { DisplayAnnouncement(value); return; }
        Send(InteractionPacket.GMAnnouncement, value.Write);
    }
    private static void DisplayAnnouncement(GMAnnouncement value) {
        Color color = new(value.R, value.G, value.B, value.A);
        if (value.Format == GMAnnouncement.Toast) {
            ScriptToast.Show(new ToastSpec { Text = value.Text, TextColor = color, Scale = 1, FontName = "departure_mono",
                MaxWidthPercent = 90, VerticalOffset = 12, Easing = ZoneEasing.Торможение,
                FadeIn = value.FadeIn, Hold = value.Hold, FadeOut = value.FadeOut,
                Shadow = true, ShadowColor = Color.Black, ShadowOffset = new Vector2(1, 1), ShadowAlpha = .8f });
        } else if (value.Format is 7 or 10 or 11) {
            var box = value.Format == 11
                ? new GUIMessageBox("", value.Text, Array.Empty<LocalizedString>(), type: GUIMessageBox.Type.InGame)
                : new GUIMessageBox("", value.Text);
            box.Text.TextColor = color;
        } else {
            // Native client renderer handles type 11 as its own in-game message box.
            GameMain.NetworkMember?.AddChatMessage(ChatMessage.Create("GM", value.Text, (ChatMessageType)value.Format, null, textColor: color));
        }
    }
}
