#nullable enable

using System;
using System.IO;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.RPWheel
{

    internal enum Curve { Linear, EaseOutBack, EaseInCubic, EaseOutCubic, SmoothStep }

    internal static class AnimationCurve
    {
        public static float Evaluate(Curve curve, float t) => curve switch
        {
            Curve.EaseOutBack  => EaseOutBack(t),
            Curve.EaseInCubic  => t * t * t,
            Curve.EaseOutCubic => 1f - MathF.Pow(1f - t, 3f),
            Curve.SmoothStep   => t * t * (3f - 2f * t),
            _                  => t
        };

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            return 1f + c3 * MathF.Pow(t - 1f, 3f) + c1 * MathF.Pow(t - 1f, 2f);
        }
    }

    internal static class Theme
    {
        public static readonly Color PanelDark      = new Color(30,  30,  34,  255);
        public static readonly Color PanelBg        = new Color(37,  37,  42,  255);
        public static readonly Color NanoGold       = new Color(230, 235, 245, 255);
        public static readonly Color NanoGoldBright = new Color(255, 255, 255, 255);
        public static readonly Color ButtonDefault  = new Color(70,  73,  102, 128);
        public static readonly Color ButtonHovered  = new Color(87,  91,  127, 128);
        public static readonly Color ButtonPressed  = new Color(62,  108, 69,  255);
        public static readonly Color ButtonDisabled = new Color(48,  49,  60,  255);
        public static readonly Color CautionDefault = new Color(171, 50,  50,  255);
        public static readonly Color CautionHover   = new Color(207, 47,  47,  255);
        public static readonly Color CloseIdle      = new Color(120, 130, 150, 255);
        public static readonly Color CloseHover     = new Color(255, 100, 100, 255);
        public static readonly Color BorderDefault  = new Color(173, 216, 230, 70);
        public static readonly Color BorderHover    = new Color(173, 216, 230, 180);
        public static readonly Color Separator      = new Color(128, 128, 128, 128);
        public static readonly Color GlowBase       = new Color(173, 216, 230, 255);
        public static readonly Color TextShadow     = new Color(0,   0,   0,   140);
        public static readonly Color VignetteDark   = new Color(12,  12,  16,  200);
        public static readonly Color VignetteClear  = new Color(12,  12,  16,  0);

        public const float BorderThickness    = 2.0f;
        public const float SeparatorThickness = 1.5f;
        public const float GlowExpandOuter    = 12f;
        public const float GlowExpandInner    = 6f;
        public const float GlowAlphaMax       = 0.15f;
        public const float CrossLineWidth     = 0.072f;
        public const float CrossSize          = 0.30f;

        public const uint FontSizeLabel = 16;
        public const uint FontSizeBold  = 18;

        public static ScalableFont? LabelFont     { get; private set; }
        public static ScalableFont? LabelFontBold { get; private set; }

        public static void Initialize()
        {
            LabelFont     = PrimaryPackage.Fonts.FontRegistry.Get("NotoSans-Regular".ToIdentifier(), FontSizeLabel, true);
            LabelFontBold = PrimaryPackage.Fonts.FontRegistry.Get("NotoSans-Bold".ToIdentifier(), FontSizeBold, true);
        }

        public static void Dispose()
        {
            LabelFont = null;
            LabelFontBold = null;
        }
    }

    internal sealed class Renderer
    {
        private const int ArcSegmentsPerRevolution = 1024;

        private const int GlowVertCapacity   = 4096;
        private const int SectorVertCapacity = 2048;
        private const int StripVertCapacity  = 4096;
        private const int LineVertCapacity   = 4;

        private readonly VertexPositionColor[] _glowVerts  = new VertexPositionColor[GlowVertCapacity];
        private readonly VertexPositionColor[] _innerTrans = new VertexPositionColor[SectorVertCapacity];
        private readonly VertexPositionColor[] _innerSolid = new VertexPositionColor[SectorVertCapacity];
        private readonly VertexPositionColor[] _outerSolid = new VertexPositionColor[SectorVertCapacity];
        private readonly VertexPositionColor[] _outerTrans = new VertexPositionColor[SectorVertCapacity];
        private readonly VertexPositionColor[] _stripVerts = new VertexPositionColor[StripVertCapacity];
        private readonly VertexPositionColor[] _lineVerts1 = new VertexPositionColor[LineVertCapacity];
        private readonly VertexPositionColor[] _lineVerts2 = new VertexPositionColor[LineVertCapacity];
        private readonly VertexPositionColor[] _lineVerts3 = new VertexPositionColor[LineVertCapacity];

        public void DrawGlowAnnulusSector(GraphicsDevice gd, Vector2 center,
            float innerR, float outerR, float angleFrom, float angleTo,
            Color glowColor, float hoverProgress)
        {
            if (hoverProgress < 0.01f) return;
            float expandOuter = Theme.GlowExpandOuter * hoverProgress;
            float expandInner = Theme.GlowExpandInner * hoverProgress;
            byte  maxAlpha    = (byte)(Theme.GlowAlphaMax * 255f * hoverProgress);
            var   full        = new Color(glowColor.R, glowColor.G, glowColor.B, maxAlpha);
            var   transparent = new Color(glowColor.R, glowColor.G, glowColor.B, (byte)0);
            float range       = angleTo - angleFrom;
            int   segCount    = Math.Max(2, (int)(range / (MathF.Tau / ArcSegmentsPerRevolution)) + 1);
            float step        = range / (segCount - 1);
            DrawGlowBand(gd, center, innerR - expandInner, innerR,               angleFrom, segCount, step, transparent, full);
            DrawGlowBand(gd, center, innerR,               outerR,               angleFrom, segCount, step, full,        full);
            DrawGlowBand(gd, center, outerR,               outerR + expandOuter, angleFrom, segCount, step, full,        transparent);
        }

        private void DrawGlowBand(GraphicsDevice gd, Vector2 center,
            float r0, float r1, float angleFrom, int segCount, float step,
            Color innerColor, Color outerColor)
        {
            int required = segCount * 2;
            if (required > _glowVerts.Length) return;
            for (int i = 0; i < segCount; i++)
            {
                float a  = angleFrom + step * i;
                var unit = new Vector2(MathF.Cos(a), -MathF.Sin(a));
                _glowVerts[i * 2]     = new VertexPositionColor(new Vector3(center + unit * r1, 0f), outerColor);
                _glowVerts[i * 2 + 1] = new VertexPositionColor(new Vector3(center + unit * r0, 0f), innerColor);
            }
            if (segCount >= 2)
                gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, _glowVerts, 0, (segCount - 1) * 2);
        }

        public void DrawThickSeparator(GraphicsDevice gd, Vector2 center,
            float innerR, float outerR, float angle, Color color, float thickness)
        {
            var unit = new Vector2(MathF.Cos(angle), -MathF.Sin(angle));
            DrawThickLine(gd, center + unit * innerR, center + unit * outerR, thickness * 0.5f, color);
        }

        public void DrawThickArcBorder(GraphicsDevice gd, Vector2 center,
            float radius, float angleFrom, float angleTo, Color color, float thickness)
        {
            DrawAnnulusSectorAA(gd, center,
                radius - thickness * 0.5f, radius + thickness * 0.5f,
                angleFrom, angleTo, color, 1.0f);
        }

        public void DrawAnnulusSectorAA(GraphicsDevice gd, Vector2 center,
            float rInner, float rOuter, float angleFrom, float angleTo, Color color, float feather = 1.0f)
        {
            if (rInner < 0f) rInner = 0f;
            float range    = angleTo - angleFrom;
            int   segCount = Math.Max(2, (int)(range / (MathF.Tau / ArcSegmentsPerRevolution)) + 1);
            if (segCount > _innerTrans.Length) return;
            float step      = range / (segCount - 1);
            var   colSolid  = color;
            var   colTrans  = new Color(color.R, color.G, color.B, (byte)0);
            float ri0 = MathF.Max(0f, rInner - feather);
            float ri1 = rInner;
            float ro1 = rOuter;
            float ro0 = rOuter + feather;
            for (int i = 0; i < segCount; i++)
            {
                float a    = angleFrom + step * i;
                var unit   = new Vector2(MathF.Cos(a), -MathF.Sin(a));
                var dir    = new Vector3(unit, 0f);
                _innerTrans[i] = new VertexPositionColor(new Vector3(center, 0f) + dir * ri0, rInner > 0.001f ? colTrans : colSolid);
                _innerSolid[i] = new VertexPositionColor(new Vector3(center, 0f) + dir * ri1, colSolid);
                _outerSolid[i] = new VertexPositionColor(new Vector3(center, 0f) + dir * ro1, colSolid);
                _outerTrans[i] = new VertexPositionColor(new Vector3(center, 0f) + dir * ro0, colTrans);
            }
            if (rInner > 0.001f) DrawStrip(gd, _innerSolid, _innerTrans, segCount);
            DrawStrip(gd, _outerSolid, _innerSolid, segCount);
            DrawStrip(gd, _outerTrans, _outerSolid, segCount);
        }

        private void DrawStrip(GraphicsDevice gd,
            VertexPositionColor[] outer, VertexPositionColor[] inner, int count)
        {
            int required = count * 2;
            if (required > _stripVerts.Length) return;
            for (int i = 0; i < count; i++)
            {
                _stripVerts[i * 2]     = outer[i];
                _stripVerts[i * 2 + 1] = inner[i];
            }
            gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, _stripVerts, 0, (count - 1) * 2);
        }

        public void DrawThickLine(GraphicsDevice gd, Vector2 a, Vector2 b, float hw, Color col)
        {
            var dir = b - a;
            float len = dir.Length();
            if (len < 0.001f) return;
            dir /= len;
            var n = new Vector2(-dir.Y, dir.X);
            const float Feather = 1.0f;
            float wSolid = hw;
            float wTrans = hw + Feather;
            var colSolid = col;
            var colTrans = new Color(col.R, col.G, col.B, (byte)0);
            var vL0_a = new VertexPositionColor(new Vector3(a - n * wTrans, 0f), colTrans);
            var vL0_b = new VertexPositionColor(new Vector3(b - n * wTrans, 0f), colTrans);
            var vL1_a = new VertexPositionColor(new Vector3(a - n * wSolid, 0f), colSolid);
            var vL1_b = new VertexPositionColor(new Vector3(b - n * wSolid, 0f), colSolid);
            var vR1_a = new VertexPositionColor(new Vector3(a + n * wSolid, 0f), colSolid);
            var vR1_b = new VertexPositionColor(new Vector3(b + n * wSolid, 0f), colSolid);
            var vR0_a = new VertexPositionColor(new Vector3(a + n * wTrans, 0f), colTrans);
            var vR0_b = new VertexPositionColor(new Vector3(b + n * wTrans, 0f), colTrans);
            _lineVerts1[0] = vL0_a; _lineVerts1[1] = vL1_a; _lineVerts1[2] = vL0_b; _lineVerts1[3] = vL1_b;
            gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, _lineVerts1, 0, 2);
            _lineVerts2[0] = vL1_a; _lineVerts2[1] = vR1_a; _lineVerts2[2] = vL1_b; _lineVerts2[3] = vR1_b;
            gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, _lineVerts2, 0, 2);
            _lineVerts3[0] = vR1_a; _lineVerts3[1] = vR0_a; _lineVerts3[2] = vR1_b; _lineVerts3[3] = vR0_b;
            gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, _lineVerts3, 0, 2);
        }
    }

    internal enum WheelState { Closed, Opening, Open, Closing }

    internal sealed class WheelOverlay
    {
        private const float InitialRadius       = 100f;
        private const float RadiusIncrement     = 5f;
        private const float InnerMult           = 0.5f;
        private const float OuterMult           = 1.5f;
        private const float AngleOffset         = MathF.PI * 0.5f;
        private const float HoverSpeed          = 10f;
        private const float OpenDuration        = 0.22f;
        private const float CloseDuration       = 0.14f;
        private const float MaxDeltaTime        = 0.1f;
        private const float FullCircleAngle     = MathF.Tau - 0.001f;
        private const float SectorFillExpand    = 2f;
        private const float CursorDotRadius     = 4f;
        private const float CursorDotFeather    = 1.5f;
        private const float CursorShadowOffset  = 1.5f;
        private const float CursorShadowAlpha   = 0.55f;
        private const float CursorDotLarger     = 5.5f;
        private const float AlphaFadeInEnd      = 0.60f;
        private const float VignetteMaxAlphaByte = 245f;
        private const float VignetteFalloffPower = 2.5f;
        private const float VignetteEdgeMargin  = 16f;
        private const float LabelWidthFillFactor = 0.85f;
        private const float LabelDepthShadow    = 0.09f;
        private const float LabelDepthText      = 0.10f;
        private const float HoverCloseFactor    = 2f;

        private const int VignetteRings       = 4;
        private const int VignetteSegments    = 32;
        private const int VignetteVertexCount = VignetteRings * (VignetteSegments + 1) * 2;

        private static readonly Vector2[] _vigUnitCircle = BuildVigUnitCircle();

        private const int MaxSectors = 32;

        private readonly VertexPositionColor[] _vigVerts      = new VertexPositionColor[VignetteVertexCount];
        private readonly string[]              _labels;
        private readonly int                   _count;
        private readonly float                 _innerRadius;
        private readonly float                 _outerRadius;
        private readonly float                 _sectorAngle;
        private readonly Action                _onClose;
        private readonly float[]               _hoverProgress;
        private readonly float[]               _sectorFrom;
        private readonly float[]               _sectorTo;
        private readonly Vector2[]             _sectorMidUnit;
        private readonly Vector2[]             _labelSizes;
        private bool                           _labelCacheValid;

        private readonly Renderer _renderer = new();

        private BasicEffect? _effect;
        private Matrix       _projection;
        private int          _projGw;
        private int          _projGh;

        private WheelState _state = WheelState.Closed;
        private int        _hoveredSector;
        private bool       _centerHovered;
        private Vector2    _center;
        private float      _animTime;

        internal float AnimScale;
        internal float AnimAlpha;

        public bool IsOpen => _state != WheelState.Closed;

        public WheelOverlay(EmoteDef[] defs, Action onClose)
        {
            _count         = Math.Min(defs.Length, MaxSectors);
            _labels        = new string[_count];
            for (int i = 0; i < _count; i++) _labels[i] = TextManager.Get(defs[i].NameKey).Value;
            _onClose       = onClose;
            _hoverProgress = new float[Math.Max(1, _count)];
            float cr       = InitialRadius + _count * RadiusIncrement;
            _innerRadius   = cr * InnerMult;
            _outerRadius   = cr * OuterMult;
            _sectorAngle   = _count > 0 ? MathF.Tau / _count : MathF.Tau;
            _hoveredSector = -1;

            _sectorFrom    = new float[_count];
            _sectorTo      = new float[_count];
            _sectorMidUnit = new Vector2[_count];
            _labelSizes    = new Vector2[_count];

            for (int i = 0; i < _count; i++)
            {
                _sectorFrom[i]    = _sectorAngle * i + AngleOffset;
                _sectorTo[i]      = _sectorAngle * (i + 1) + AngleOffset;
                float mid         = _sectorFrom[i] + _sectorAngle * 0.5f;
                _sectorMidUnit[i] = new Vector2(MathF.Cos(mid), -MathF.Sin(mid));
            }

            RebuildLabelCache();
        }

        private void RebuildLabelCache()
        {
            var font = Theme.LabelFont ?? GUIStyle.SmallFont?.Value;
            if (font == null) { _labelCacheValid = false; return; }
            for (int i = 0; i < _count; i++)
                _labelSizes[i] = font.MeasureString(_labels[i]);
            _labelCacheValid = true;
        }

        public void Open()
        {
            if (_count == 0 || _state != WheelState.Closed) return;
            TransitionTo(WheelState.Opening);
        }

        public void Close()
        {
            if (_state == WheelState.Closed || _state == WheelState.Closing) return;
            TransitionTo(WheelState.Closing);
        }

        private void TransitionTo(WheelState next)
        {
            _state = next;
            switch (next)
            {
                case WheelState.Opening:
                    _animTime       = 0f;
                    AnimScale       = 0f;
                    AnimAlpha       = 0f;
                    _hoveredSector  = -1;
                    _centerHovered  = false;
                    Array.Clear(_hoverProgress, 0, _hoverProgress.Length);
                    break;
                case WheelState.Open:
                    AnimScale = 1f;
                    AnimAlpha = 1f;
                    break;
                case WheelState.Closing:
                    _animTime      = 0f;
                    _hoveredSector = -1;
                    _centerHovered = false;
                    break;
                case WheelState.Closed:
                    AnimScale = 0f;
                    AnimAlpha = 0f;
                    break;
            }
        }

        internal void Cleanup()
        {
            if (_effect != null && !_effect.IsDisposed) _effect.Dispose();
            _effect = null;
        }

        public void Tick(float dt, bool hotkey, bool esc)
        {
            if (_state == WheelState.Closed) return;
            if (dt < 0f) dt = 0f;
            if (dt > MaxDeltaTime) dt = MaxDeltaTime;
            UpdateAnimation(dt);
            if (_state == WheelState.Closing) return;

            _center        = new Vector2(GameMain.GraphicsWidth * 0.5f, GameMain.GraphicsHeight * 0.5f);
            var mousePos   = PlayerInput.MousePosition;
            float ir       = _innerRadius * GUI.Scale;
            _centerHovered = (mousePos - _center).LengthSquared() < ir * ir;
            _hoveredSector = _centerHovered ? -1 : GetSectorAt(mousePos);

            if (esc) { _onClose(); return; }

            if (!hotkey)
            {
                if (!_centerHovered && _hoveredSector >= 0) EmoteNet.Send((byte)_hoveredSector);
                _onClose();
            }
        }

        private void UpdateAnimation(float dt)
        {
            if (_state == WheelState.Closing)
            {
                _animTime += dt;
                float t    = Math.Clamp(_animTime / CloseDuration, 0f, 1f);
                AnimScale  = 1f - AnimationCurve.Evaluate(Curve.EaseInCubic, t);
                AnimAlpha  = 1f - t;
                if (t >= 1f) { TransitionTo(WheelState.Closed); return; }
                float decay = MathF.Exp(-HoverSpeed * HoverCloseFactor * dt);
                for (int i = 0; i < _count; i++)
                {
                    _hoverProgress[i] *= decay;
                    if (_hoverProgress[i] < 0.001f) _hoverProgress[i] = 0f;
                }
            }
            else
            {
                _animTime += dt;
                float t    = Math.Clamp(_animTime / OpenDuration, 0f, 1f);
                AnimScale  = AnimationCurve.Evaluate(Curve.EaseOutBack, t);
                AnimAlpha  = Math.Clamp(t / AlphaFadeInEnd, 0f, 1f);
                if (t >= 1f && _state == WheelState.Opening) TransitionTo(WheelState.Open);
                for (int i = 0; i < _count; i++)
                {
                    float target = (i == _hoveredSector) ? 1f : 0f;
                    _hoverProgress[i] = target + (_hoverProgress[i] - target) * MathF.Exp(-HoverSpeed * dt);
                    if (MathF.Abs(_hoverProgress[i] - target) < 0.001f) _hoverProgress[i] = target;
                }
            }
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            if (_state == WheelState.Closed) return;
            if (AnimScale <= 0.001f || AnimAlpha <= 0f) return;
            int gw = GameMain.GraphicsWidth;
            int gh = GameMain.GraphicsHeight;
            if (gw <= 0 || gh <= 0) return;
            _center = new Vector2(gw * 0.5f, gh * 0.5f);
            var gd  = spriteBatch.GraphicsDevice;
            EnsureEffect(gd);
            EnsureProjection(gw, gh);
            var prevRast  = gd.RasterizerState;
            var prevBlend = gd.BlendState;
            var prevDS    = gd.DepthStencilState;
            gd.RasterizerState   = RasterizerState.CullNone;
            gd.BlendState        = BlendState.NonPremultiplied;
            gd.DepthStencilState = DepthStencilState.None;
            _effect!.Projection  = _projection;
            try
            {
                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    DrawGeometry(gd, AnimScale, AnimAlpha, gw, gh);
                    DrawCursorDot(gd, AnimAlpha);
                }
            }
            finally
            {
                gd.RasterizerState   = prevRast;
                gd.BlendState        = prevBlend;
                gd.DepthStencilState = prevDS;
            }
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable);
            try { DrawTextLabels(spriteBatch, AnimScale, AnimAlpha); }
            finally { spriteBatch.End(); }
        }

        private void EnsureProjection(int gw, int gh)
        {
            if (gw == _projGw && gh == _projGh) return;
            _projection = Matrix.CreateOrthographicOffCenter(0, gw, gh, 0, 0, 1);
            _projGw = gw;
            _projGh = gh;
        }

        private static Vector2[] BuildVigUnitCircle()
        {
            var arr = new Vector2[VignetteSegments + 1];
            for (int i = 0; i <= VignetteSegments; i++)
            {
                float angle = MathF.Tau * i / VignetteSegments;
                arr[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            }
            return arr;
        }

        private void DrawVignette(GraphicsDevice gd, float alpha, int gw, int gh)
        {
            float R       = MathF.Sqrt(gw * gw + gh * gh) * 0.5f + VignetteEdgeMargin;
            byte maxAlpha = (byte)Math.Clamp((int)(VignetteMaxAlphaByte * alpha), 0, 255);
            int  vIdx     = 0;
            for (int ring = 0; ring < VignetteRings; ring++)
            {
                float x0 = (float)ring       / VignetteRings;
                float x1 = (float)(ring + 1) / VignetteRings;
                float r0 = x0 * R;
                float r1 = x1 * R;
                byte  a0 = (byte)(maxAlpha * MathF.Pow(x0, VignetteFalloffPower));
                byte  a1 = (byte)(maxAlpha * MathF.Pow(x1, VignetteFalloffPower));
                var col0 = new Color(0, 0, 0, (int)a0);
                var col1 = new Color(0, 0, 0, (int)a1);
                for (int i = 0; i <= VignetteSegments; i++)
                {
                    var unit = _vigUnitCircle[i];
                    _vigVerts[vIdx++] = new VertexPositionColor(new Vector3(_center + unit * r1, 0f), col1);
                    _vigVerts[vIdx++] = new VertexPositionColor(new Vector3(_center + unit * r0, 0f), col0);
                }
            }
            System.Diagnostics.Debug.Assert(vIdx == VignetteVertexCount,
                $"DrawVignette wrote {vIdx} verts but _vigVerts has {VignetteVertexCount}");
            for (int ring = 0; ring < VignetteRings; ring++)
            {
                int startVertex    = ring * (VignetteSegments + 1) * 2;
                int primitiveCount = VignetteSegments * 2;
                gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, _vigVerts, startVertex, primitiveCount);
            }
        }

        private void DrawGeometry(GraphicsDevice gd, float scale, float alpha, int gw, int gh)
        {
            DrawVignette(gd, alpha, gw, gh);
            float currentScale = scale * GUI.Scale;
            float innerR       = _innerRadius * currentScale;
            float outerR       = _outerRadius * currentScale;

            for (int i = 0; i < _count; i++)
            {
                if (_hoverProgress[i] < 0.01f) continue;
                _renderer.DrawGlowAnnulusSector(gd, _center, innerR, outerR,
                    _sectorFrom[i], _sectorTo[i], Theme.GlowBase, _hoverProgress[i]);
            }
            for (int i = 0; i < _count; i++)
            {
                var fill = Fade(Color.Lerp(Theme.ButtonDefault, Theme.ButtonHovered, _hoverProgress[i]), alpha);
                _renderer.DrawAnnulusSectorAA(gd, _center,
                    MathF.Max(0f, innerR - SectorFillExpand * currentScale),
                    outerR + SectorFillExpand * currentScale,
                    _sectorFrom[i], _sectorTo[i], fill, 1.0f);
            }

            _renderer.DrawAnnulusSectorAA(gd, _center, 0f, innerR, 0f, FullCircleAngle,
                Fade(Theme.PanelDark, alpha), 1.0f);

            var sep = Fade(Theme.Separator, alpha);
            for (int i = 0; i < _count; i++)
                _renderer.DrawThickSeparator(gd, _center, innerR, outerR,
                    _sectorFrom[i], sep, Theme.SeparatorThickness * GUI.Scale);

            for (int i = 0; i < _count; i++)
            {
                var borderCol = Fade(Color.Lerp(Theme.BorderDefault, Theme.BorderHover, _hoverProgress[i]), alpha);
                _renderer.DrawThickArcBorder(gd, _center, outerR, _sectorFrom[i], _sectorTo[i], borderCol, Theme.BorderThickness * GUI.Scale);
                _renderer.DrawThickArcBorder(gd, _center, innerR, _sectorFrom[i], _sectorTo[i], borderCol, Theme.BorderThickness * GUI.Scale);
            }

            DrawCrossIcon(gd, scale, alpha);
        }

        private void DrawCursorDot(GraphicsDevice gd, float alpha)
        {
            var   mousePos    = PlayerInput.MousePosition;
            float s           = GUI.Scale;
            float shadowAlpha = alpha * CursorShadowAlpha;
            var   shadowCol   = new Color(0, 0, 0, (int)(255 * shadowAlpha));
            var   dotCol      = new Color(255, 255, 255, (int)(255 * alpha));
            _renderer.DrawAnnulusSectorAA(gd,
                mousePos + new Vector2(CursorShadowOffset * s, CursorShadowOffset * s),
                0f, CursorDotLarger * s, 0f, FullCircleAngle, shadowCol, SectorFillExpand * s);
            _renderer.DrawAnnulusSectorAA(gd, mousePos, 0f, CursorDotRadius * s,
                0f, FullCircleAngle, dotCol, CursorDotFeather * s);
        }

        private void DrawCrossIcon(GraphicsDevice gd, float scale, float alpha)
        {
            float currentScale = scale * GUI.Scale;
            float h  = _innerRadius * currentScale * Theme.CrossSize;
            float lw = _innerRadius * currentScale * Theme.CrossLineWidth;
            byte  a  = (byte)Math.Clamp((int)(255 * alpha), 0, 255);
            var col  = _centerHovered
                ? new Color(Theme.CloseHover.R, Theme.CloseHover.G, Theme.CloseHover.B, a)
                : new Color(Theme.CloseIdle.R,  Theme.CloseIdle.G,  Theme.CloseIdle.B,  a);
            _renderer.DrawThickLine(gd, _center + new Vector2(-h, -h), _center + new Vector2( h,  h), lw, col);
            _renderer.DrawThickLine(gd, _center + new Vector2( h, -h), _center + new Vector2(-h,  h), lw, col);
        }

        private void DrawTextLabels(SpriteBatch sb, float scale, float alpha)
        {
            var font = Theme.LabelFont ?? GUIStyle.SmallFont.Value;
            if (font == null) return;
            if (!_labelCacheValid) RebuildLabelCache();
            float uiScale         = GUI.Scale;
            float innerR          = _innerRadius * scale * uiScale;
            float outerR          = _outerRadius * scale * uiScale;
            float midR            = (innerR + outerR) * 0.5f;
            float maxAllowedWidth = (outerR - innerR) * LabelWidthFillFactor;
            for (int i = 0; i < _count; i++)
            {
                var sectorCenter = _center + _sectorMidUnit[i] * midR;
                string text        = _labels[i];
                var   sz           = _labelCacheValid ? _labelSizes[i] : font.MeasureString(text);
                float scaleFactor  = 1.0f;
                float labelWidth   = sz.X * uiScale;
                if (labelWidth > maxAllowedWidth && maxAllowedWidth > 0f)
                    scaleFactor = maxAllowedWidth / labelWidth;
                float finalScale = uiScale * scaleFactor;
                float hy         = sz.Y * 0.5f * finalScale;
                var pos          = new Vector2(MathF.Round(sectorCenter.X), MathF.Round(sectorCenter.Y - hy));
                var shadowCol    = Fade(Theme.TextShadow, alpha);
                font.DrawString(sb, text, pos + new Vector2(1f, 1f) * uiScale,
                    shadowCol, 0f, Vector2.Zero, finalScale, SpriteEffects.None, LabelDepthShadow,
                    alignment: Alignment.CenterX);
                var textCol = Fade(Color.Lerp(Theme.NanoGold, Theme.NanoGoldBright, _hoverProgress[i]), alpha);
                font.DrawString(sb, text, pos,
                    textCol, 0f, Vector2.Zero, finalScale, SpriteEffects.None, LabelDepthText,
                    alignment: Alignment.CenterX);
            }
        }

        private int GetSectorAt(Vector2 point)
        {
            if (_count <= 0) return -1;
            var diff = point - _center;
            if (diff.LengthSquared() < 4f) return -1;
            float angle = MathF.Atan2(-diff.Y, diff.X) - AngleOffset;
            angle = ((angle % MathF.Tau) + MathF.Tau) % MathF.Tau;
            int s = (int)(angle / _sectorAngle);
            return Math.Clamp(s, 0, _count - 1);
        }

        private static Color Fade(Color c, float a) => new(c.R, c.G, c.B, (int)(c.A * a));

        private void EnsureEffect(GraphicsDevice gd)
        {
            if (_effect != null && !_effect.IsDisposed) return;
            _effect?.Dispose();
            _effect = new BasicEffect(gd)
            {
                VertexColorEnabled = true,
                World              = Matrix.Identity,
                View               = Matrix.Identity,
            };
        }
    }
}
