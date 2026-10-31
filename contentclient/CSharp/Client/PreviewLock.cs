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
            if (!protection || info is null || GameMain.Client is null) { return false; }

            // The visual lock must NOT depend on whether this client has the .sub file,
            // has already loaded its XML, or possesses a content lease. Those states
            // describe file availability, not whether the server permits a preview.
            // The wire protocol currently advertises a global protected-preview
            // policy, not a per-submarine allowlist. Mask all network previews
            // including active/previously decrypted submarine files.
            // This presentation policy is not a security boundary.
            return true;
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
