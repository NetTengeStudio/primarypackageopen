using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;

namespace PrimaryPackage.TextMotion;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif

internal static class ChatPresentation {
    internal const int MaxReveals = 16, MaxPopups = 8, MaxChats = 4;
    private sealed class Reveal {
        internal GUITextBlock Block;
        internal GUIComponent Holder;
        internal TextVisual Visual;
        internal TypePresentation Presentation;
        internal TypeClickCursor Clicks;
        internal double Received;
        internal bool Cancelled;
    }
    private sealed class Popup {
        internal TextVisual Body, Sender;
        internal GUIComponent NativeFallback;
        internal Color Ink, SenderInk;
        internal double Received;
        internal PopupLifetime Life;
        internal TypeClickCursor Clicks;
        internal TextSpring Y;
    }
    private sealed class Session {
        internal ChatBox Chat;
        internal GUICustomComponent Overlay;
        internal readonly List<Reveal> Reveals = new List<Reveal>(MaxReveals);
        internal readonly List<Popup> Popups = new List<Popup>(MaxPopups);
        internal bool Following, ScrollWritten;
        internal bool RestoreNativePopups;
        internal float LastScroll;
    }
    internal readonly struct Admission {
        internal readonly GUIComponent Last;
        internal readonly int PopupCount;
        internal readonly bool Pinned;
        internal Admission(ChatBox chat) {
            Last = LastMessage(chat); PopupCount = nativePopups(chat).Count;
            Pinned = chat.chatBox.BarSize >= 1 || chat.chatBox.BarScroll >= 0.97f;
        }
    }

    private static readonly AccessTools.FieldRef<ChatBox, List<GUIComponent>> nativePopups =
        AccessTools.FieldRefAccess<ChatBox, List<GUIComponent>>("popupMessages");
    private static readonly List<Session> sessions = new List<Session>(MaxChats);
    private static readonly Dictionary<GUITextBlock, Reveal> tracked = new Dictionary<GUITextBlock, Reveal>(MaxChats * MaxReveals);
    private static readonly TextPainter painter = new TextPainter();

    internal static void Attach(Harmony harmony) {
        harmony.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.AddMessage), new[] { typeof(ChatMessage) }),
            prefix: ModuleHost.Bind(typeof(ChatPresentation), nameof(AddPrefix)),
            postfix: ModuleHost.Bind(typeof(ChatPresentation), nameof(AddPostfix)));
        harmony.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.Update), new[] { typeof(float) }),
            postfix: ModuleHost.Bind(typeof(ChatPresentation), nameof(UpdatePostfix)));
        harmony.Patch(AccessTools.Method(typeof(ChatBox), "UpdateMessageAnimation"),
            prefix: ModuleHost.Bind(typeof(ChatPresentation), nameof(SkipMessageShake)));
        harmony.Patch(AccessTools.Method(typeof(GUITextBlock), "Draw", new[] { typeof(SpriteBatch) }),
            transpiler: ModuleHost.Bind(typeof(ChatTextDrawPatch), nameof(ChatTextDrawPatch.Transpile)));
    }

    internal static void Reset() {
        for (int i = 0; i < sessions.Count; i++) { Drop(sessions[i]); }
        sessions.Clear(); tracked.Clear();
    }

    
    
    public static bool SkipMessageShake(out IEnumerable<CoroutineStatus> __result) {
        __result = Array.Empty<CoroutineStatus>(); return false;
    }

    public static void AddPrefix(ChatBox __instance, out Admission __state) => __state = new Admission(__instance);

    public static void AddPostfix(ChatBox __instance, ChatMessage message, Admission __state) {
        GUIComponent holder = LastMessage(__instance);
        if (holder == null || ReferenceEquals(holder, __state.Last) || message == null) { return; }
        GUITextBlock block = TextOf(holder);
        if (block?.Font?.Value == null || block.Text.Length == 0 || block.Text.Length > TextVisual.MaxTextLength) { return; }
        Session session = Find(__instance);
        try {
            if (__instance.ToggleOpen) {
                if (TypeSettings.Pace.Instant || tracked.ContainsKey(block)) { return; }
                var visual = new TextVisual(block.Text.SanitizedValue, TypeSettings.Pace, TextVisual.Colors(block.RichTextData));
                visual.Follow(visual.FontFor(block.Font), block.WrappedText.Value);
                var reveal = new Reveal { Block = block, Holder = holder, Visual = visual, Received = HudClock.Now };
                if (session.Reveals.Count == MaxReveals) { Retire(session, 0); }
                session.Reveals.Add(reveal); tracked.Add(block, reveal);
                
                session.Following = __state.Pinned || session.Following && session.ScrollWritten &&
                    MathF.Abs(__instance.chatBox.BarScroll - session.LastScroll) < 0.005f;
                session.ScrollWritten = false;
                if (session.Following) { FollowScroll(session, reveal, HudClock.Now); }
            } else {
                List<GUIComponent> native = nativePopups(__instance);
                if (native.Count != __state.PopupCount + 1) { return; }
                GUIComponent nativePopup = native[native.Count - 1];
                var body = new TextVisual(block.Text.SanitizedValue, TypeSettings.Pace, TextVisual.Colors(block.RichTextData));
                body.Wrap(body.FontFor(block.Font), MathF.Max(1, __instance.GUIFrame.Rect.Width * 0.95f), 12);
                string sender = string.IsNullOrWhiteSpace(message.SenderName) ? "" :
                    (message.Type == ChatMessageType.Private ? "[PM] " : "") + message.SenderName;
                if (sender.Length > TextVisual.MaxTextLength) { return; }
                var instant = new TypePace { Speed = 1, Instant = true };
                var name = new TextVisual(sender, instant);
                var popup = new Popup { Body = body, Sender = name, NativeFallback = nativePopup, Ink = message.Color, Received = HudClock.Now,
                    SenderInk = message.SenderCharacter?.Info?.Job is { } job ? Color.Lerp(job.Prefab.UIColor, Color.White, 0.25f) : Color.White };
                if (session.Overlay == null) {
                    session.Overlay = new GUICustomComponent(new RectTransform(Point.Zero, __instance.GUIFrame.RectTransform),
                        (sb, _) => PaintPopups(sb, session)) { CanBeFocused = false };
                }
                if (session.Popups.Count == MaxPopups) { session.Popups.RemoveAt(0); }
                session.Popups.Add(popup);
                
                native.RemoveAt(native.Count - 1);
                nativePopup.Parent?.RemoveChild(nativePopup);
            }
        } catch (Exception error) {
            ModLog.Once("TextMotion chat admission failed; native message retained: " + error);
        }
    }

    public static void UpdatePostfix(ChatBox __instance, float deltaTime) {
        Session session = Existing(__instance);
        if (session == null) { return; }
        double now = HudClock.Now;
        GUIListBox list = __instance.chatBox;
        if (session.RestoreNativePopups) {
            
            if (!__instance.ToggleOpen) {
                List<GUIComponent> native = nativePopups(__instance);
                for (int i = 0; i < session.Popups.Count; i++) {
                    GUIComponent fallback = session.Popups[i].NativeFallback;
                    fallback.RectTransform.Parent = __instance.GUIFrame.RectTransform;
                    native.Add(fallback);
                }
            }
            session.Popups.Clear(); session.RestoreNativePopups = false;
        }
        if (session.Following && session.ScrollWritten && MathF.Abs(list.BarScroll - session.LastScroll) > 0.005f) {
            session.Following = false;
        }
        for (int i = session.Reveals.Count - 1; i >= 0; i--) {
            Reveal reveal = session.Reveals[i];
            if (reveal.Cancelled || !__instance.ToggleOpen || !ReferenceEquals(reveal.Holder.Parent, list.Content) || reveal.Block.Parent == null ||
                (reveal.Presentation.Started ? reveal.Presentation.Age(now) >= reveal.Visual.Text.Duration : now - reveal.Received >= TypeMotion.PendingSeconds)) {
                if (!reveal.Cancelled && session.Following && __instance.ToggleOpen && ReferenceEquals(LastMessage(__instance), reveal.Holder)) { WriteScroll(session, 1); }
                Retire(session, i);
            }
        }
        if (!__instance.ToggleOpen) {
            session.Following = false; session.ScrollWritten = false;
            for (int i = session.Popups.Count - 1; i >= 0; i--) {
                Popup popup = session.Popups[i];
                if (!popup.Life.Update(now, popup.Received, popup.Body.Text.Duration, i == 0, session.Popups.Count)) { session.Popups.RemoveAt(i); }
            }
            return;
        }
        session.Popups.Clear();
        if (!session.Following || session.Reveals.Count == 0) { return; }
        Reveal newest = session.Reveals[session.Reveals.Count - 1];
        if (!ReferenceEquals(LastMessage(__instance), newest.Holder)) { return; }
        FollowScroll(session, newest, now);
    }

    private static void FollowScroll(Session session, Reveal newest, double now) {
        GUIListBox list = session.Chat.chatBox;
        float range = list.TotalSize - list.Content.Rect.Height;
        if (range <= 1) { return; }
        float shown = newest.Visual.Layout.HeightAt(newest.Visual.Text, newest.Presentation.Age(now));
        float hidden = MathF.Max(0, (newest.Visual.Layout.Height - shown) * newest.Block.TextScale);
        WriteScroll(session, Math.Clamp((range - hidden) / range, 0, 1));
    }

    
    
    public static bool IsTextEmpty(string text, GUITextBlock block, SpriteBatch spriteBatch) {
        if (string.IsNullOrEmpty(text)) { return true; }
        if (!tracked.TryGetValue(block, out Reveal reveal)) { return false; }
        if (!block.Visible || Rectangle.Intersect(block.Rect, spriteBatch.GraphicsDevice.ScissorRectangle).IsEmpty) { return true; }
        ScalableFont font = reveal.Visual.FontFor(block.Font);
        if (font == null || !string.Equals(block.Text.SanitizedValue, reveal.Visual.Text.Plain, StringComparison.Ordinal)) {
            reveal.Cancelled = true; tracked.Remove(block); return false;
        }
        try {
            reveal.Visual.Follow(font, text);
            double now = HudClock.Now;
            reveal.Presentation.Present(now);
            Vector2 pos = block.Rect.Location.ToVector2() + block.TextPos + block.TextOffset;
            if (block.RoundToNearestPixel) { pos = new Vector2((int)pos.X, (int)pos.Y); }
            pos -= block.Origin * block.TextScale;
            Color ink = block.State is GUIComponent.ComponentState.Hover or GUIComponent.ComponentState.HoverSelected ? block.HoverTextColor : block.TextColor;
            if (!block.Enabled) { ink = block.DisabledTextColor; }
            else if (block.State == GUIComponent.ComponentState.Selected) { ink = block.SelectedTextColor; }
            float richAlpha = block.OverrideRichTextDataAlpha ? ink.A / 255.0f : -1;
            ink *= ink.A / 255.0f;
            painter.Begin(spriteBatch, true);
            try { painter.Draw(reveal.Visual, pos, reveal.Presentation.Age(now), ink, 1, block.TextScale, block.Shadow, richAlpha); }
            finally { painter.End(); }
            TypeSound.Present(ref reveal.Clicks, reveal.Visual.Text, reveal.Visual.Layout.Count, reveal.Presentation.Age(now), 1, now);
            return true;
        } catch (Exception error) {
            tracked.Remove(block);
            reveal.Cancelled = true;
            ModLog.Once("TextMotion reveal failed; native text restored: " + error);
            return false;
        }
    }

    private static void PaintPopups(SpriteBatch sb, Session session) {
        try { PaintPopupsCore(sb, session); }
        catch (Exception error) {
            session.RestoreNativePopups = true;
            ModLog.Once("TextMotion popup render failed; native popup ownership will be restored: " + error);
        }
    }

    private static void PaintPopupsCore(SpriteBatch sb, Session session) {
        if (session.Chat.ToggleOpen || session.Popups.Count == 0) { return; }
        ScalableFont font = GUIStyle.SmallFont?.Value;
        if (font == null) { return; }
        double now = HudClock.Now;
        float scale = GUI.Scale, pad = 6 * scale, gap = 10 * scale;
        Rectangle frame = session.Chat.GUIFrame.Rect;
        float maxWidth = MathF.Max(1, MathF.Min(frame.Width * 0.95f - pad * 2, GameMain.GraphicsWidth - pad * 2));
        float reports = GameMain.GameSession?.CrewManager?.ReportButtonFrame?.Rect.Width ?? 0;
        float x = frame.Right + reports + 35 * scale, y = frame.Y;
        painter.Begin(sb, true);
        try {
            for (int i = 0; i < session.Popups.Count; i++) {
                Popup popup = session.Popups[i];
                popup.Body.Wrap(popup.Body.FontFor(GUIStyle.SmallFont), maxWidth, 12); popup.Sender.Wrap(popup.Sender.FontFor(GUIStyle.SmallFont), maxWidth, 2);
                popup.Life.Presentation.Present(now);
                float age = popup.Life.Presentation.Age(now), textAge = age - TypeMotion.RevealLead;
                float enter = TypeMotion.Ease(age / TypeMotion.EnterSeconds);
                float leave = popup.Life.Leaving ? TypeMotion.Ease((float)(now - popup.Life.LeaveAt) / TypeMotion.LeaveSeconds) : 0;
                float alpha = popup.Life.Opacity(now);
                float nameHeight = popup.Sender.Text.Count > 0 ? popup.Sender.Layout.Height : 0;
                float height = MathF.Min(GameMain.GraphicsHeight, popup.Body.Layout.HeightAt(popup.Body.Text, textAge) + nameHeight + pad * 2);
                float width = MathF.Min(GameMain.GraphicsWidth, MathF.Max(popup.Body.Layout.Width, popup.Sender.Layout.Width) + pad * 2);
                float py = popup.Y.Follow(y, now, 18);
                var box = new Box(Math.Clamp(x - scale * (12 * (1 - enter) + 16 * leave), 0, MathF.Max(0, GameMain.GraphicsWidth - width)),
                    Math.Clamp(py, 0, MathF.Max(0, GameMain.GraphicsHeight - height)), width, height);
                Rectangle clip = painter.Clip(box);
                painter.Fill(box, new Rgba(0, 0, 0, 212).Fade(alpha));
                painter.Frame(box, scale, new Rgba(105, 105, 105).Fade(alpha));
                painter.Draw(popup.Sender, new Vector2(box.X + pad, box.Y + pad), 0, popup.SenderInk, alpha);
                painter.Draw(popup.Body, new Vector2(box.X + pad, box.Y + pad + nameHeight), textAge, popup.Ink, alpha);
                painter.Scissor(clip);
                TypeSound.Present(ref popup.Clicks, popup.Body.Text, popup.Body.Layout.Count, textAge, alpha, now);
                y += (height + gap) * (1 - leave);
            }
        } finally { painter.End(); }
    }

    private static GUIComponent LastMessage(ChatBox chat) {
        GUIComponent content = chat.chatBox?.Content;
        return content == null || content.CountChildren == 0 ? null : content.GetChild(content.CountChildren - 1);
    }
    private static GUITextBlock TextOf(GUIComponent holder) {
        foreach (GUIComponent child in holder.Children) { if (child is GUITextBlock block && block.Wrap) { return block; } }
        return null;
    }
    private static Session Existing(ChatBox chat) {
        for (int i = 0; i < sessions.Count; i++) { if (ReferenceEquals(sessions[i].Chat, chat)) { return sessions[i]; } }
        return null;
    }
    private static Session Find(ChatBox chat) {
        Session session = Existing(chat);
        if (session != null) { return session; }
        if (sessions.Count == MaxChats) { Drop(sessions[0]); sessions.RemoveAt(0); }
        session = new Session { Chat = chat }; sessions.Add(session); return session;
    }
    private static void Retire(Session session, int i) { tracked.Remove(session.Reveals[i].Block); session.Reveals.RemoveAt(i); }
    private static void Drop(Session session) {
        for (int i = session.Reveals.Count - 1; i >= 0; i--) { Retire(session, i); }
        session.Popups.Clear(); session.Overlay?.Parent?.RemoveChild(session.Overlay); session.Overlay = null;
    }
    private static void WriteScroll(Session session, float value) {
        session.Chat.chatBox.BarScroll = value; session.LastScroll = session.Chat.chatBox.BarScroll; session.ScrollWritten = true;
    }
}
