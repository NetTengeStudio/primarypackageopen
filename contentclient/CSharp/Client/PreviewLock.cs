using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SubSeal.Wire;

namespace SubSeal.Client
{
    internal static class PreviewLock
    {
        internal const string Text = "ого.";

        private static LeaseStore leases;
        private static bool protection;
        private static bool refreshPending;
        private static bool creating;
        private static bool heldLocked;

        internal static bool ProtectionActive => protection;

        internal static bool IsCreating => creating;

        internal static bool IsHeldLocked() => heldLocked;

        internal static void Bind(LeaseStore store) => leases = store;

        internal static void SetPolicy(byte flags)
        {
            bool now = (flags & Protocol.PolicyPreviewProtected) != 0;
            if (now == protection) { return; }
            protection = now;
            refreshPending = true;
        }

        internal static void Clear()
        {
            protection = false;
            refreshPending = false;
            creating = false;
            heldLocked = false;
        }

        internal static bool ConsumeRefresh()
        {
            bool pending = refreshPending;
            refreshPending = false;
            return pending;
        }

        internal static void BeginCreate(bool locked)
        {
            creating = true;
            heldLocked = locked;
        }

        internal static void EndCreate() => creating = false;

        internal static void Release()
        {
            heldLocked = false;
            creating = false;
        }

        internal static bool ShowLocked(SubmarineInfo info) => protection && IsLocked(info);

        internal static bool IsLocked(SubmarineInfo info)
        {
            if (info is null) { return false; }

            bool loaded = !info.LazyLoad && info.SubmarineElement is not null;
            bool corrupted = info.IsFileCorrupted;
            bool exists = !string.IsNullOrEmpty(info.FilePath) && System.IO.File.Exists(info.FilePath);
            bool sealedFile = exists && SealedFile.CarriesSeal(info.FilePath);

            if (corrupted) { return protection; }
            if (!Decide(protection, corrupted, exists, sealedFile, loaded)) { return false; }
            if (!sealedFile || !loaded) { return true; }

            if (!SubmarineSkeleton.IsReduced(info.SubmarineElement)) { return false; }
            if (!SealedFile.TryReadLeaseId(info.FilePath, out uint leaseId)) { return true; }
            return leases is null || !leases.TryGet(leaseId, out _);
        }

        internal static bool Decide(bool protectedContent, bool corrupted, bool fileExists, bool sealedFile, bool elementLoaded)
            => protectedContent && (corrupted || !fileExists || (sealedFile && !elementLoaded));

        internal static void Draw(SpriteBatch spriteBatch, Rectangle area)
        {
            GUI.DrawRectangle(spriteBatch, area, Color.Black, isFilled: true);
            LocalizedString text = Text;
            Vector2 size = GUIStyle.LargeFont.MeasureString(text);
            Vector2 center = new Vector2(area.Center.X, area.Center.Y);
            GUIStyle.LargeFont.DrawString(spriteBatch, text, center, Color.White, 0f, size * 0.5f, 1f, SpriteEffects.None, 0f);
        }

        internal static void RefreshLobby()
        {
            NetLobbyScreen lobby = GameMain.NetLobbyScreen;
            GameClient client = GameMain.Client;
            if (lobby?.SubList is null || client?.ServerSubmarines is null) { return; }

            lobby.UpdateSubList(lobby.SubList, client.ServerSubmarines);

            if (lobby.ShuttleList?.ListBox is not null)
            {
                System.Collections.Generic.List<SubmarineInfo> shuttles = new System.Collections.Generic.List<SubmarineInfo>();
                foreach (SubmarineInfo sub in client.ServerSubmarines)
                {
                    if (sub.HasTag(SubmarineTag.Shuttle)) { shuttles.Add(sub); }
                }
                lobby.UpdateSubList(lobby.ShuttleList.ListBox, shuttles);
            }

            SubmarineSelection.ContentRefreshRequired = true;
        }
    }
}
