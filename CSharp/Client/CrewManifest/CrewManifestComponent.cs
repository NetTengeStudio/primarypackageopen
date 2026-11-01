using System;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.CrewManifest {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public partial class CrewManifestComponent {
        private GUIListBox jobListBox;
        private GUITextBlock cardInfoText;
        private Identifier selectedJob;
        private ItemContainer idSlot;
        private Item lastSeenCard;
        partial void InitProjSpecific() {
            BuildUI();
            idSlot = item.GetComponent<ItemContainer>();
            idSlot?.OnContainedItemsChanged.RegisterOverwriteExisting(
                "erc_crewmanifest_cardslot".ToIdentifier(), _ => OnCardSlotChanged());
        }
        public override void OnItemLoaded() {
            base.OnItemLoaded();
            if (ManifestState.OnJobChangedLocal == null) { ManifestState.OnJobChangedLocal = RefreshCrewEntry; }
            if (GameMain.Client != null && !ManifestState.ClientFullSyncRequested) {
                ManifestState.ClientFullSyncRequested = true;
                item.CreateClientEvent(this, new FullSyncRequestEventData());
            }
        }
        private void BuildUI() {
            if (GuiFrame == null) { return; }
            GuiFrame.ClearChildren();
            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.9f), GuiFrame.RectTransform, Anchor.Center))
            { Stretch = true, RelativeSpacing = 0.02f };
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), content.RectTransform),
                "Контроллер Манифеста Экипажа", font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);
            cardInfoText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.08f), content.RectTransform), "",
                textAlignment: Alignment.Center, wrap: true);
            jobListBox = new GUIListBox(new RectTransform(new Vector2(1f, 0.62f), content.RectTransform))
            { OnSelected = (comp, data) => { selectedJob = (Identifier)data; return true; } };
            PopulateJobs();
            RefreshCardInfo();
            new GUIButton(new RectTransform(new Vector2(1f, 0.12f), content.RectTransform), "Назначить роль") {
                OnClicked = (b, d) => {
                    if (!selectedJob.IsEmpty) { DoRequest(selectedJob); }
                    return true;
                }
            };
        }
        public override bool Select(Character character) {
            if (character == Character.Controlled) {
                RefreshCardInfo();
                PlayOpenSound();
            }
            return base.Select(character);
        }
        private void OnCardSlotChanged() {
            Item current = idSlot?.Inventory?.AllItems?.FirstOrDefault();
            if (current != lastSeenCard && current != null) {
                PlayCardInsertSound();
            }
            lastSeenCard = current;
            RefreshCardInfo();
        }
        private void RefreshCardInfo() {
            if (cardInfoText == null) { return; }
            if (ManifestLogic.TryGetTargetFromInsertedCard(item, out var target, out var idCard, out string error)) {
                string roleName = target.Info?.Job?.Name?.Value ?? "";
                cardInfoText.Text = $"Карта: {idCard.OwnerName} ({roleName})";
                cardInfoText.TextColor = Color.LightGreen;
            }
            else {
                cardInfoText.Text = error ?? "Вставьте ID-карту участника экипажа";
                cardInfoText.TextColor = Color.Red;
            }
        }
        private void PopulateJobs() {
            if (jobListBox == null) { return; }
            jobListBox.Content.ClearChildren();
            foreach (var jobPrefab in JobPrefab.Prefabs.Where(j => !j.HiddenJob).OrderBy(j => j.Name.Value)) {
                var entry = new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), jobListBox.Content.RectTransform),
                    jobPrefab.Name.Value, textColor: jobPrefab.UIColor)
                { UserData = jobPrefab.Identifier };
                entry.RectTransform.MinSize = new Point(0, 24);
            }
        }
        private void DoRequest(Identifier jobId) {
            if (!ManifestLogic.HasCaptainAccess(Character.Controlled, out string permError)) {
                GUI.AddMessage(permError ?? "Отказано", Color.Red);
                PlayAccessDeniedSound();
                return;
            }
            if (GameMain.NetworkMember == null) {
                if (ManifestLogic.TryApply(item, Character.Controlled, jobId, out string error)) {
                    PlayConfirmSound();
                    RefreshCardInfo();
                }
                else {
                    GUI.AddMessage(error ?? "Отказано", Color.Red);
                    PlayAccessDeniedSound();
                }
                return;
            }
            item.CreateClientEvent(this, new RequestEventData(jobId));
        }
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            byte kind = msg.ReadByte();
            if (kind == 0) {
                ReadApplyOne(msg);
                PlayConfirmSound();
                RefreshCardInfo();
            }
            else { int count = msg.ReadUInt16(); for (int i = 0; i < count; i++) { ReadApplyOne(msg); } }
        }
        private static void ReadApplyOne(IReadMessage msg) {
            ushort targetId = msg.ReadUInt16();
            Identifier original = ReadId(msg);
            Identifier current = ReadId(msg);
            if (Entity.FindEntityByID(targetId) is Character target) { ManifestLogic.ApplyFromSync(target, original, current); }
        }
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData) {
            if (TryExtractEventData<RequestEventData>(extraData, out var req)) {
                msg.WriteByte((byte)0);
                WriteId(msg, req.JobId);
            }
            else { msg.WriteByte((byte)1); }
        }
        private static void RefreshCrewEntry(Character c) {
            var crewManager = GameMain.GameSession?.CrewManager;
            if (crewManager == null || c == null) { return; }
            try { crewManager.RemoveCharacterFromCrewList(c); crewManager.AddCharacterToCrewList(c); }
            catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("CrewManifestComponent.1", noted); }
        }
        private void PlaySound(string file, float volume, GUISoundType fallback) {
            string modDir = PrimaryPackage.Core.ModAssets.Root;
            Sound custom = string.IsNullOrEmpty(modDir)
                ? null
                : PrimaryPackage.Core.SoundStore.Absolute(Path.Combine(modDir, "Content", "Sounds", "UI", file));
            if (custom != null) { custom.Play(volume, SoundManager.SoundCategoryUi); }
            else { SoundPlayer.PlayUISound(fallback); }
        }
        private void PlayCardInsertSound()   => PlaySound("idcard_insert.ogg",    0.8f, GUISoundType.PopupMenu);
        private void PlayAccessDeniedSound() => PlaySound("access_denied.ogg",    0.8f, GUISoundType.PickItemFail);
        private void PlayOpenSound()         => PlaySound("manifest_open.ogg",    0.7f, GUISoundType.PopupMenu);
        private void PlayConfirmSound()      => PlaySound("manifest_confirm.ogg", 0.8f, GUISoundType.UISwitch);
    }
}
