using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;

namespace PrimaryPackage.TextMotion;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif


public static class Speech {
    public const int MaxTalkers = 24, PerTalker = 3, MaxLines = 6;
    private const float FadeSeconds = 0.45f;
    private sealed class Line {
        internal TextVisual Visual;
        internal Color Ink;
        internal double Received;
        internal TypePresentation Presentation;
        internal TypeClickCursor Clicks;
        internal TextSpring Bottom;
    }
    private sealed class Talker {
        internal Character Who;
        internal object Key;
        internal Vector2 Anchor;
        internal bool Seen, Gone;
        internal double GoneAt;
        internal TextSpring Shown;
        internal readonly Line[] Lines = new Line[PerTalker];
        internal int Count;
    }
    private static readonly List<Talker> talkers = new List<Talker>(MaxTalkers);
    
    
    private static readonly ConcurrentDictionary<Character, byte> speechClaims = new ConcurrentDictionary<Character, byte>();
    private static readonly TextPainter painter = new TextPainter();
    public static int Active => talkers.Count;

    internal static void Attach(Harmony harmony) {
        harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ShowSpeechBubble), new[] { typeof(Color), typeof(string) }),
            prefix: ModuleHost.Bind(typeof(Speech), nameof(ShowPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.DrawSpeechBubbles), new[] { typeof(SpriteBatch), typeof(Camera) }),
            postfix: ModuleHost.Bind(typeof(Speech), nameof(DrawPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ShowTextlessSpeechBubble), new[] { typeof(float), typeof(Color) }),
            prefix: ModuleHost.Bind(typeof(Speech), nameof(TextlessPrefix)));
    }
    internal static void Reset() { talkers.Clear(); speechClaims.Clear(); }
    public static bool TextlessPrefix(Character __instance) => __instance == null || !speechClaims.ContainsKey(__instance);

    public static bool ShowPrefix(Character __instance, Color color, string text) {
        if (IsEmote(text)) { return true; }
        if (__instance == null || !__instance.IsHuman || __instance.Removed || __instance.IsDead ||
            !GameSettings.CurrentConfig.ChatSpeechBubbles || GUIStyle.SmallFont?.Value == null) { return true; }
        try {
            if (Push(__instance, null, text, color) == null) { return true; }
            __instance.textlessSpeechBubble = null;
            return false;
        } catch (Exception error) {
            ModLog.Once("TextMotion speech admission failed; native speech retained: " + error);
            return true;
        }
    }

    private static bool IsEmote(string text) {
        if (string.IsNullOrEmpty(text)) { return false; }
        int start = 0;
        int end = text.Length - 1;
        while (start <= end && char.IsWhiteSpace(text[start])) { start++; }
        while (end >= start && char.IsWhiteSpace(text[end])) { end--; }
        return end - start >= 2 && text[start] == '*' && text[end] == '*';
    }

    public static void Say(Character speaker, string text) {
        if (speaker != null && !speaker.Removed) { Push(speaker, null, text, Color.White); }
    }
    public static void SayAt(object key, Vector2 anchor, string text) {
        if (key == null || !float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y)) { return; }
        Talker talker = Push(null, key, text, Color.White);
        if (talker != null) { talker.Anchor = anchor; talker.Seen = true; }
    }
    public static void Hold(object key, Vector2 anchor, bool seen) {
        AssertMainThread();
        Talker talker = Find(null, key);
        if (talker == null || !float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y)) { return; }
        talker.Anchor = anchor; talker.Seen = seen;
    }
    public static void Drop(object key) {
        AssertMainThread();
        Talker talker = Find(null, key);
        if (talker != null && !talker.Gone) { talker.Gone = true; talker.GoneAt = HudClock.Now; }
    }

    private static Talker Push(Character who, object key, string text, Color ink) {
        AssertMainThread();
        if (string.IsNullOrWhiteSpace(text) || text.Length > TextVisual.MaxTextLength) { return null; }
        TextVisual visual = TextVisual.Rich(text.Trim());
        if (visual.Text.Count == 0) { return null; }
        double now = HudClock.Now;
        var line = new Line { Visual = visual, Ink = ink, Received = now };
        Talker talker = Find(who, key);
        if (talker == null) {
            if (talkers.Count == MaxTalkers) {
                int oldest = 0;
                for (int i = 1; i < talkers.Count; i++) {
                    if (talkers[i].Lines[talkers[i].Count - 1].Received < talkers[oldest].Lines[talkers[oldest].Count - 1].Received) { oldest = i; }
                }
                ReleaseClaim(talkers[oldest]); talkers.RemoveAt(oldest);
            }
            talker = new Talker { Who = who, Key = key };
            talker.Shown.Snap(0, now);
            talkers.Add(talker);
        }
        if (talker.Count == PerTalker) {
            Array.Copy(talker.Lines, 1, talker.Lines, 0, PerTalker - 1);
            talker.Count--;
        }
        talker.Lines[talker.Count++] = line;
        talker.Gone = false;
        if (who != null) { speechClaims.TryAdd(who, 0); }
        return talker;
    }

    private static Talker Find(Character who, object key) {
        if (who == null && key == null) { return null; }
        for (int i = 0; i < talkers.Count; i++) {
            Talker talker = talkers[i];
            if (key != null ? ReferenceEquals(talker.Key, key) : talker.Key == null && ReferenceEquals(talker.Who, who)) { return talker; }
        }
        return null;
    }

    public static void DrawPostfix(SpriteBatch spriteBatch, Camera cam) {
        if (talkers.Count == 0) { return; }
        double now = HudClock.Now;
        for (int i = talkers.Count - 1; i >= 0; i--) { if (!Advance(talkers[i], now)) { ReleaseClaim(talkers[i]); talkers.RemoveAt(i); } }
        ScalableFont font = GUIStyle.SmallFont?.Value;
        if (GUI.DisableHUD || cam == null || font == null || talkers.Count == 0) { return; }
        try {
            painter.Begin(spriteBatch, false);
            try { for (int i = 0; i < talkers.Count; i++) { Draw(talkers[i], cam, font, now); } }
            finally { painter.End(); }
        } catch (Exception error) {
            ModLog.Once("TextMotion speech render failed: " + error);
            Reset();
        }
    }

    private static bool Advance(Talker talker, double now) {
        int keep = 0;
        for (int i = 0; i < talker.Count; i++) {
            Line line = talker.Lines[i];
            bool alive = line.Presentation.Started ? line.Presentation.Age(now) < Life(line) : now - line.Received < TypeMotion.PendingSeconds;
            if (alive) { talker.Lines[keep++] = line; }
        }
        Array.Clear(talker.Lines, keep, talker.Count - keep); talker.Count = keep;
        if (keep == 0) { return false; }
        if (talker.Key != null) { return !talker.Gone || now - talker.GoneAt < FadeSeconds; }
        Character who = talker.Who;
        bool exists = who != null && !who.Removed && !who.IsDead;
        if (!exists && !talker.Gone) { talker.Gone = true; talker.GoneAt = now; ReleaseClaim(talker); }
        if (exists) {
            Limb head = who.AnimController?.GetLimb(LimbType.Head);
            talker.Anchor = head?.body != null ? head.body.DrawPosition : who.DrawPosition + new Vector2(0, 100);
        }
        talker.Seen = exists && GameSettings.CurrentConfig.ChatSpeechBubbles && who.Enabled && who.InvisibleTimer <= 0 &&
            (Character.Controlled == null || ReferenceEquals(who, Character.Controlled) || who.hudInfoVisible);
        return !talker.Gone || now - talker.GoneAt < FadeSeconds;
    }

    private static void Draw(Talker talker, Camera cam, ScalableFont font, double now) {
        float shown = Math.Clamp(talker.Shown.Follow(talker.Seen && !talker.Gone ? 1 : 0, now, 16), 0, 1);
        if (talker.Gone) { shown *= 1 - TypeMotion.Ease((float)(now - talker.GoneAt) / FadeSeconds); }
        if (shown < 0.001f) { return; }
        float scale = GUI.Scale;
        Vector2 anchor = cam.WorldToScreen(talker.Anchor + new Vector2(0, 34));
        float width = MathF.Min(300 * scale, MathF.Max(1, GameMain.GraphicsWidth - 10 * scale));
        if (anchor.X < -width || anchor.X > GameMain.GraphicsWidth + width || anchor.Y < 0 ||
            anchor.Y > GameMain.GraphicsHeight + MaxLines * font.LineHeight) { return; }
        float pad = 5 * scale, gap = 4 * scale, bottom = 0;
        for (int i = talker.Count - 1; i >= 0; i--) {
            Line line = talker.Lines[i];
            line.Visual.Wrap(line.Visual.FontFor(GUIStyle.SmallFont), width, MaxLines);
            line.Presentation.Present(now);
            float age = line.Presentation.Age(now), textAge = age - TypeMotion.RevealLead;
            float enter = TypeMotion.Ease(age / TypeMotion.EnterSeconds);
            float leave = TypeMotion.Ease((Life(line) - age) / FadeSeconds);
            float alpha = shown * enter * leave;
            float height = line.Visual.Layout.HeightAt(line.Visual.Text, textAge) + pad * 2;
            float offset = line.Bottom.Follow(bottom, now, 22);
            float boxWidth = line.Visual.Layout.Width + pad * 2;
            var box = new Box(Math.Clamp(anchor.X - boxWidth * 0.5f, 0, MathF.Max(0, GameMain.GraphicsWidth - boxWidth)),
                anchor.Y - offset - height + (1 - enter) * pad, boxWidth, height);
            Rectangle clip = painter.Clip(box);
            painter.Fill(box, new Rgba(0, 0, 0, 150).Fade(alpha));
            painter.Frame(box, scale, new Rgba(105, 105, 105, 153).Fade(alpha));
            painter.Draw(line.Visual, new Vector2(box.X + pad, box.Y + pad), textAge, line.Ink, alpha, shadow: true);
            painter.Scissor(clip);
            TypeSound.Present(ref line.Clicks, line.Visual.Text, line.Visual.Layout.Count, textAge, alpha, now);
            bottom += (height + gap) * leave;
        }
    }

    private static float Life(Line line) => TypeMotion.RevealLead + line.Visual.Text.Duration + TypeMotion.ReadSeconds(line.Visual.Text) + FadeSeconds;

    private static void AssertMainThread() {
        if (GameMain.MainThread != null && Thread.CurrentThread != GameMain.MainThread) {
            throw new InvalidOperationException("Speech presentation must be accessed on the game main thread");
        }
    }
    private static void ReleaseClaim(Talker talker) { if (talker.Who != null) { speechClaims.TryRemove(talker.Who, out _); } }
}
