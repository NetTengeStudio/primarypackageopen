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

            if (!protection) { return false; }

            bool loaded = !info.LazyLoad && info.SubmarineElement is not null;
            bool exists = !string.IsNullOrEmpty(info.FilePath) && System.IO.File.Exists(info.FilePath);
            // An unreadable sealed file must not be treated as ordinary plaintext.
            SealProbe probe = exists ? SealedFile.Probe(info.FilePath) : SealProbe.Absent;
            bool reduced = loaded && SubmarineSkeleton.IsReduced(info.SubmarineElement);
            bool hasKey = false;

            if (probe == SealProbe.Present && reduced &&
                SealedFile.TryReadLeaseId(info.FilePath, out uint leaseId))
            {
                hasKey = leases is not null && leases.TryGet(leaseId, out _);
            }

            return PreviewDecision.ShouldLock(
                protection, info.IsFileCorrupted, exists,
                probe == SealProbe.Present, probe == SealProbe.Unreadable,
                loaded, reduced, hasKey);
        }

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
