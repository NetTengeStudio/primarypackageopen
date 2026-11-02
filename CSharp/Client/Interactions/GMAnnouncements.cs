using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    private readonly Dictionary<Guid, (GMAnnouncement Message, double Started)> pendingAnnouncements = new();
    private readonly List<Guid> expiredAnnouncements = new(8);
    private InteractionRecovery announcementRecovery;
    private InteractionRecovery singleplayerAnnouncementRecovery;
    private bool announcementDirty;
    private double saveAnnouncementAt;
    internal GMMessageState AnnouncementState { get; private set; } = new();
    internal string AnnouncementStatus { get; private set; } = "Черновик сохраняется автоматически";
    internal event Action AnnouncementsChanged;
    private byte announcementFormats;
    internal bool AllowsAnnouncement(byte format) => (GMAnnouncement.FormatBit(format) & (Multiplayer ? announcementFormats : GMAnnouncement.AllFormats)) != 0;
    internal void EnsureAnnouncementRecovery() {
        if (!Multiplayer) {
            singleplayerAnnouncementRecovery ??= new InteractionRecovery("singleplayer", "local");
            if (announcementRecovery != singleplayerAnnouncementRecovery) { BindAnnouncements(singleplayerAnnouncementRecovery); }
        } else {
            if (recovery == null) { throw new IOException("Локальное хранилище сообщений недоступно. Проверьте Data/Interactions"); }
            if (announcementRecovery != recovery) { BindAnnouncements(recovery); }
        }
    }
    private void TryBindAnnouncements(InteractionRecovery next) {
        try { BindAnnouncements(next); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AnnouncementStatus = error.Message; Core.ModLog.Error(error.Message); AnnouncementsChanged?.Invoke(); }
    }
    private void BindAnnouncements(InteractionRecovery next) {
        SaveAnnouncements(true);
        if (announcementDirty && announcementRecovery != null) { throw new IOException("Сначала необходимо сохранить GM-черновик предыдущего подключения. Проверьте доступ к Data/Interactions"); }
        pendingAnnouncements.Clear();
        announcementRecovery = next; AnnouncementState = next.Data.GM.Copy(); announcementDirty = false;
        AnnouncementStatus = "Черновик восстановлен";
        AnnouncementsChanged?.Invoke();
    }
    internal void ChangeAnnouncementDraft() {
        if (!announcementDirty) { saveAnnouncementAt = GUI.GUIRuntime.Now + .4; }
        announcementDirty = true;
    }
    internal void SaveAnnouncements(bool force = false) {
        if (!announcementDirty || announcementRecovery == null || !force && GUI.GUIRuntime.Now < saveAnnouncementAt) { return; }
        try { announcementRecovery.SaveGM(AnnouncementState); announcementDirty = false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            saveAnnouncementAt = GUI.GUIRuntime.Now + 5;
            AnnouncementStatus = "Не удалось сохранить черновик: " + error.Message;
            Core.ModLog.Error(AnnouncementStatus); AnnouncementsChanged?.Invoke();
        }
    }
    private void ResetAnnouncements() { SaveAnnouncements(true); pendingAnnouncements.Clear(); }
    private void UpdateAnnouncements() {
        SaveAnnouncements();
        if (Multiplayer && Connected && recovery != null && announcementRecovery != recovery && !announcementDirty) { TryBindAnnouncements(recovery); }
        if (pendingAnnouncements.Count == 0) { return; }
        expiredAnnouncements.Clear();
        foreach (var pending in pendingAnnouncements) { if (GUI.GUIRuntime.Now - pending.Value.Started > 60) { expiredAnnouncements.Add(pending.Key); } }
        if (expiredAnnouncements.Count == 0) { return; }
        foreach (Guid id in expiredAnnouncements) { pendingAnnouncements.Remove(id); }
        AnnouncementStatus = "Подтверждение не получено. Сообщение могло быть отправлено; автоматического повтора нет";
        AnnouncementsChanged?.Invoke();
    }
    internal void Announce(GMAnnouncement value, bool preview) {
        value.Validate();
        if (!Allows(InteractionAction.SendGMMessage)) { throw new UnauthorizedAccessException("Нет разрешения на GM-сообщения"); }
        if (!AllowsAnnouncement(value.Format)) { throw new UnauthorizedAccessException("Нет разрешения на выбранный формат сообщения"); }
        EnsureAnnouncementRecovery();
        if (preview || !Multiplayer) {
            Peer player = null;
            if (value.HasSender && value.Sender == GMMessageSender.Player) {
                foreach (var peer in Peers) { if (peer.Account == value.SenderAccount) { player = peer; break; } }
                if (player == null) { throw new InvalidOperationException("Выбранный отправитель отключился"); }
            }
            string playerName = player != null && Entity.FindEntityByID(player.Character) is Character body ? body.DisplayName : player?.Name;
            string name = value.ResolveSender(GameMain.Client?.ServerName ?? "Сервер", Character.Controlled?.DisplayName ?? GameMain.Client?.Name ?? "Игровой мастер", playerName);
            DisplayAnnouncement(value, name);
            if (!preview) { AcceptAnnouncement(value, new() { Id = value.Id, Sender = name, Recipients = 1, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); }
            return;
        }
        if (!ready || pendingAnnouncements.Count >= 8) { throw new InvalidOperationException("Дождитесь подключения или подтверждения предыдущих сообщений"); }
        if (pendingAnnouncements.ContainsKey(value.Id)) { throw new InvalidOperationException("Это сообщение уже ожидает подтверждения"); }
        var request = value.Copy();
        pendingAnnouncements.Add(request.Id, (request, GUI.GUIRuntime.Now));
        if (!Send(InteractionPacket.GMAnnouncement, request.Write)) { pendingAnnouncements.Remove(request.Id); throw new InvalidOperationException("Не удалось отправить сообщение серверу"); }
        AnnouncementStatus = "Отправлено. Ожидание подтверждения сервера…"; AnnouncementsChanged?.Invoke();
    }
    private void ReadAnnouncementReceipt(IReadMessage input) {
        var receipt = GMAnnouncementReceipt.Read(input);
        if (!pendingAnnouncements.Remove(receipt.Id, out var pending)) { return; }
        if (!receipt.Accepted) { AnnouncementStatus = receipt.Error; AnnouncementsChanged?.Invoke(); views.Error(receipt.Error); return; }
        AcceptAnnouncement(pending.Message, receipt);
    }
    private void AcceptAnnouncement(GMAnnouncement value, GMAnnouncementReceipt receipt) {
        AnnouncementState.Record(value, receipt); ChangeAnnouncementDraft();
        AnnouncementStatus = "Принято сервером · получателей: " + receipt.Recipients;
        SaveAnnouncements(true);
        AnnouncementsChanged?.Invoke();
    }
    private static void DisplayAnnouncement(GMAnnouncement value, string sender = "") {
        Color color = new(value.R, value.G, value.B, value.A);
        if (value.Format == GMAnnouncement.Toast) {
            ScriptToast.Show(new ToastSpec { Text = value.Text, TextColor = color, Scale = 1, FontName = "departure_mono",
                MaxWidthPercent = 90, VerticalOffset = 12, Easing = ZoneEasing.Торможение,
                FadeIn = value.FadeIn, Hold = value.Hold, FadeOut = value.FadeOut,
                Shadow = true, ShadowColor = Color.Black, ShadowOffset = new Vector2(1, 1), ShadowAlpha = .8f });
        } else if (value.Format is 7 or 10 or 11) {
            string text = value.NativeText(sender);
            if (value.Format is 10 or 11) { text = TextManager.GetServerMessage(text).Value; }
            var box = value.Format == 11
                ? new GUIMessageBox("", text, Array.Empty<LocalizedString>(), type: GUIMessageBox.Type.InGame)
                : new GUIMessageBox("", text);
            box.Text.TextColor = color;
        } else {
            // Native client renderer handles type 11 as its own in-game message box.
            GameMain.NetworkMember?.AddChatMessage(ChatMessage.Create(sender, value.Text, (ChatMessageType)value.NativeFormat, null, textColor: color));
        }
    }
}
