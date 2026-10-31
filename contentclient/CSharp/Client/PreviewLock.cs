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

        private static bool policyKnown;
        private static bool protection;
        private static bool creating;
        private static bool heldLocked;
        private static bool previewUiReady;
        private static bool refreshPending;
        private static bool closePending;

        internal static bool ProtectionActive => GameMain.Client is not null && (!policyKnown || protection);
        internal static bool ConfirmedProtected => policyKnown && protection;
        internal static bool PreviewUiReady => previewUiReady;
        internal static bool IsCreating => creating;

        internal static bool IsHeldLocked() => heldLocked || ProtectionActive;

        internal static void SetUiReady(bool ready)
        {
            previewUiReady = ready;
        }

        internal static void SetPolicy(byte flags)
        {
            bool wasProtected = ProtectionActive;
            bool now = (flags & Protocol.PolicyPreviewProtected) != 0;
            bool changed = !policyKnown || protection != now;
            policyKnown = true;
            protection = now;
            if (!changed) { return; }

            refreshPending = true;
            closePending |= wasProtected != ProtectionActive;
        }

        internal static void Clear()
        {
            bool wasProtected = ProtectionActive;
            policyKnown = false;
            protection = false;
            creating = false;
            heldLocked = false;
            refreshPending = true;
            closePending |= wasProtected != ProtectionActive;
        }

        internal static void Shutdown()
        {
            policyKnown = true;
            protection = false;
            creating = false;
            heldLocked = false;
            refreshPending = false;
            closePending = false;
            PreviewSurfacePatches.SetProtection(false);
            SubmarinePreview.Close();
        }

        internal static bool ConsumeRefresh()
        {
            if (!refreshPending) { return false; }
            refreshPending = false;
            PreviewSurfacePatches.SetProtection(ProtectionActive);
            if (closePending)
            {
                closePending = false;
                SubmarinePreview.Close();
            }
            return true;
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

        internal static bool ShowLocked(SubmarineInfo info)
            => info is not null && ProtectionActive;

        internal static void Draw(SpriteBatch spriteBatch, Rectangle area)
        {
            GUI.DrawRectangle(spriteBatch, area, Color.Black, isFilled: true);
            LocalizedString text = Text;
            Vector2 size = GUIStyle.LargeFont.MeasureString(text);
            Vector2 center = new Vector2(area.Center.X, area.Center.Y);
            GUIStyle.LargeFont.DrawString(spriteBatch, text, center,
                Color.White, 0f, size * 0.5f, 1f, SpriteEffects.None, 0f);
        }

        internal static void RefreshLobby()
        {
            SubmarineSelection.ContentRefreshRequired = true;
            NetLobbyScreen lobby = GameMain.NetLobbyScreen;
            GameClient client = GameMain.Client;
            if (lobby?.SubList is null || client?.ServerSubmarines is null) { return; }

            lobby.UpdateSubList(lobby.SubList, client.ServerSubmarines);
            if (lobby.ShuttleList?.ListBox is not null)
            {
                System.Collections.Generic.List<SubmarineInfo> shuttles =
                    new System.Collections.Generic.List<SubmarineInfo>();
                foreach (SubmarineInfo sub in client.ServerSubmarines)
                {
                    if (sub.HasTag(SubmarineTag.Shuttle)) { shuttles.Add(sub); }
                }
                lobby.UpdateSubList(lobby.ShuttleList.ListBox, shuttles);
            }
        }
    }
}
