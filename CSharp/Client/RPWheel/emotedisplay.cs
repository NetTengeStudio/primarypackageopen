#nullable enable

using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.RPWheel
{
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    internal sealed class EmoteDisplay
    {
        private sealed class Bubble
        {
            public WeakReference<Character> Char = new(null!);
            public string Text  = "";
            public float  Width, Height;
            public double DeathTime;
            public float  VertTarget;
            public float  VertAchieved;
        }

        private sealed class Pending
        {
            public WeakReference<Character> Char = new(null!);
            public readonly Queue<string> Msgs = new();
            public float  TimeLeft;
            public string LastText = "";
            public double LastTime = double.NegativeInfinity;
        }

        private const double TotalTime         = 4.0;
        private const float  FadeTime          = 0.25f;
        private const float  LerpCoeff         = 10f;
        private const float  SnapPx            = 0.1f;
        private const int    CapPerChar        = 4;
        private const float  BubbleDelayBase   = 0.2f;
        private const float  BubbleDelayPerChar = 0.8f / 100f;
        private const int    MaxBubbles        = 96;
        private const float  HeadGap           = 24f;
        private const float  FallbackHeadRise  = 100f;
        private const float  LineGap           = 6f;
        private const double DedupWindow       = 0.6;

        private static readonly Color TextCol   = new(225, 232, 255, 255);
        private static readonly Color ShadowCol = new(0, 0, 0, 185);

        private readonly List<Bubble>  _active      = new();
        private readonly Stack<Bubble> _bubblePool  = new();
        private readonly List<Pending> _pending     = new();
        private readonly Stack<Pending> _pendingPool = new();

        public bool HasEntries => _active.Count > 0;
        internal void Reset() {
            foreach (var bubble in _active) { bubble.Char.SetTarget(null!); bubble.Text = ""; _bubblePool.Push(bubble); }
            foreach (var pending in _pending) { pending.Char.SetTarget(null!); pending.Msgs.Clear(); pending.LastText = ""; _pendingPool.Push(pending); }
            _active.Clear(); _pending.Clear();
        }

        internal void Show(Character ch, string action)
        {
            if (ch == null || string.IsNullOrWhiteSpace(action)) return;
            Enqueue(ch, $"*{action.Trim()}*");
        }

        private void Enqueue(Character ch, string text)
        {
            double now = Timing.TotalTime;

            Pending? pend = null;
            for (int i = 0; i < _pending.Count; i++)
                if (_pending[i].Char.TryGetTarget(out var c) && c == ch) { pend = _pending[i]; break; }

            if (pend != null && pend.LastText == text && now - pend.LastTime < DedupWindow) return;
            for (int i = 0; i < _active.Count; i++)
            {
                var b = _active[i];
                if (b.Text == text && b.DeathTime - now > TotalTime - DedupWindow &&
                    b.Char.TryGetTarget(out var c) && c == ch) return;
            }

            if (pend == null)
            {
                pend = _pendingPool.Count > 0 ? _pendingPool.Pop() : new Pending();
                pend.Char.SetTarget(ch);
                pend.TimeLeft = 0f;
                _pending.Add(pend);
            }
            pend.LastText = text;
            pend.LastTime = now;
            pend.Msgs.Enqueue(text);
        }

        internal void Tick(float dt)
        {
            double now = Timing.TotalTime;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                p.TimeLeft -= dt;
                if (p.TimeLeft > 0f) continue;
                if (p.Msgs.Count == 0 || !p.Char.TryGetTarget(out var ch) || ch == null || ch.Removed)
                {
                    p.Char.SetTarget(null!);
                    p.Msgs.Clear();
                    _pendingPool.Push(p);
                    _pending.RemoveAt(i);
                    continue;
                }
                string text = p.Msgs.Dequeue();
                p.TimeLeft += BubbleDelayBase + text.Length * BubbleDelayPerChar;
                Spawn(ch, text, now);
            }

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var b = _active[i];
                if (now - b.DeathTime >= 0 || !b.Char.TryGetTarget(out var c) || c == null || c.Removed)
                {
                    b.Char.SetTarget(null!);
                    _bubblePool.Push(b);
                    _active.RemoveAt(i);
                    continue;
                }
                float diff = b.VertAchieved - b.VertTarget;
                if (MathF.Abs(diff) <= SnapPx) b.VertAchieved = b.VertTarget;
                else b.VertAchieved += (b.VertTarget - b.VertAchieved) * (LerpCoeff * dt);
            }
        }

        private void Spawn(Character ch, string text, double now)
        {
            if (_active.Count >= MaxBubbles) return;
            var font = Theme.LabelFont ?? GUIStyle.SmallFont?.Value;
            if (font == null) return;

            var   sz = font.MeasureString(text);
            float h  = sz.Y + LineGap;

            double fadeDeath = now + FadeTime;
            int chCount = 0;
            Bubble? oldest = null;
            for (int i = 0; i < _active.Count; i++)
            {
                var b = _active[i];
                if (!b.Char.TryGetTarget(out var c) || c != ch) continue;
                b.VertTarget += h;
                chCount++;
                if (oldest == null && b.DeathTime > fadeDeath) oldest = b;
            }

            var nb = _bubblePool.Count > 0 ? _bubblePool.Pop() : new Bubble();
            nb.Char.SetTarget(ch);
            nb.Text         = text;
            nb.Width        = sz.X;
            nb.Height       = h;
            nb.DeathTime    = now + TotalTime;
            nb.VertTarget   = 0f;
            nb.VertAchieved = -h;
            _active.Add(nb);

            if (chCount + 1 > CapPerChar && oldest != null)
                oldest.DeathTime = fadeDeath;
        }

        internal void Draw(SpriteBatch sb)
        {
            if (_active.Count == 0) return;
            var cam = GameMain.GameScreen?.Cam;
            if (cam == null) return;
            var font = Theme.LabelFont ?? GUIStyle.SmallFont?.Value;
            if (font == null) return;

            double now = Timing.TotalTime;
            sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, GUI.SamplerState, null, GameMain.ScissorTestEnable);
            try
            {
                for (int i = 0; i < _active.Count; i++)
                {
                    var b = _active[i];
                    if (!b.Char.TryGetTarget(out var ch) || ch == null || ch.Removed) continue;
                    float timeLeft = (float)(b.DeathTime - now);
                    float alpha    = timeLeft <= FadeTime ? timeLeft / FadeTime : 1f;
                    if (alpha <= 0.01f) continue;

                    Limb? headLimb = null;
                    if (ch.AnimController?.Limbs != null)
                    {
                        try { headLimb = ch.AnimController.GetLimb(LimbType.Head); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("emotedisplay.1", noted); }
                    }
                    float headY = headLimb?.body?.DrawPosition.Y ?? (ch.DrawPosition.Y + FallbackHeadRise);
                    var sp = cam.WorldToScreen(new Vector2(ch.DrawPosition.X, headY));
                    float top  = Round05(sp.Y - HeadGap - b.VertAchieved - b.Height);
                    float left = Round05(sp.X - b.Width * 0.5f);
                    if (sp.X < -600 || sp.X > GameMain.GraphicsWidth + 600 ||
                        top < -160 || top > GameMain.GraphicsHeight + 160) continue;

                    var pos = new Vector2(left, top);
                    font.DrawString(sb, b.Text, pos + Vector2.One, Tint(ShadowCol, alpha), ForceUpperCase.Inherit, italics: true);
                    font.DrawString(sb, b.Text, pos,               Tint(TextCol,   alpha), ForceUpperCase.Inherit, italics: true);
                }
            }
            finally { sb.End(); }
        }

        private static float Round05(float v) => MathF.Round(v * 2f) * 0.5f;

        private static Color Tint(Color c, float a) => new(c.R, c.G, c.B, (int)(c.A * a));
    }
}
