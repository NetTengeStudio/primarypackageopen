using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Thoughts {
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ThoughtFrame {
        public float Alpha;
        public int Solid;
        public float Partial;
        [MarshalAs(UnmanagedType.I1)]
        public bool Finished;
        private readonly byte _pad0;
        private readonly byte _pad1;
        private readonly byte _pad2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ThoughtLine {
        public float Severity;
        public float CharInterval;
        public float Delay;
        public float FadeIn;
        public float Hold;
        public float FadeOut;
        public float Elapsed;
        public int CharCount;
        public ushort Index;
        public ThoughtCategory Category;
        [MarshalAs(UnmanagedType.I1)]
        public bool Active;

        public float TypeDuration => CharCount * CharInterval;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(float dt) => Elapsed += dt;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Rush(float factor) {
            if (Hold > 0.4f) { Hold *= factor; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Cut() {
            float target = Delay + TypeDuration + Hold;
            if (Elapsed < target) { Elapsed = target; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ThoughtFrame Evaluate() {
            var frame = default(ThoughtFrame);
            float t = Elapsed - Delay;
            if (t <= 0.0f) { return frame; }
            float typing = TypeDuration;
            float holdEnd = typing + Hold;
            if (t >= holdEnd + FadeOut) {
                frame.Finished = true;
                return frame;
            }
            float reveal = CharInterval > 0.0f ? t / CharInterval : CharCount;
            int solid = (int)reveal;
            float partial = reveal - solid;
            if (solid >= CharCount) {
                solid = CharCount;
                partial = 0.0f;
            }
            frame.Solid = solid;
            frame.Partial = partial;
            float alpha = FadeIn > 0.0f ? Scalar.OutCubic(t / FadeIn) : 1.0f;
            if (t > holdEnd && FadeOut > 0.0f) {
                float out01 = Scalar.Smooth(1.0f - (t - holdEnd) / FadeOut);
                if (out01 < alpha) { alpha = out01; }
            }
            frame.Alpha = alpha;
            return frame;
        }
    }

    public sealed class ThoughtTimeline {
        private const int QueueLimit = 2;
        private const float BaseCharInterval = 0.055f;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Pending {
            public float Severity;
            public ushort Index;
            public ThoughtCategory Category;
            private readonly byte _pad;
        }

        private readonly Pending[] queue = new Pending[QueueLimit];
        private int queueHead;
        private int queueTail;
        private int queueCount;
        private ThoughtLine line;
        private ThoughtFrame frame;

        public bool IsActive => line.Active;
        public ThoughtCategory CurrentCategory => line.Category;
        public ushort CurrentIndex => line.Index;
        public float CurrentSeverity => line.Severity;
        public ThoughtFrame CurrentFrame => frame;
        public int QueueCount => queueCount;

        public void Reset() {
            line = default;
            frame = default;
            queueHead = 0;
            queueTail = 0;
            queueCount = 0;
        }

        public void Cut() {
            queueHead = 0;
            queueTail = 0;
            queueCount = 0;
            if (line.Active) {
                line.Cut();
            }
        }

        public bool Enqueue(ThoughtCategory category, ushort index, float severity) {
            if (queueCount >= QueueLimit) { return false; }
            queue[queueTail] = new Pending {
                Category = category,
                Index = index,
                Severity = severity
            };
            queueTail = (queueTail + 1) % QueueLimit;
            queueCount++;
            if (line.Active) {
                line.Rush(0.55f);
            }
            return true;
        }

        public bool TryPopNext(out Pending pending) {
            if (queueCount > 0) {
                pending = queue[queueHead];
                queueHead = (queueHead + 1) % QueueLimit;
                queueCount--;
                return true;
            }
            pending = default;
            return false;
        }

        public void Begin(ThoughtCategory category, ushort index, float severity, int charCount, in ThoughtSignals signals) {
            if (charCount <= 0) { return; }

            float interval = BaseCharInterval;
            if (signals.Brain > ThoughtSelector.BrainSlight) { interval *= 2.6f; }
            if (signals.Vitality < 0.3f) { interval *= 1.5f; }
            if (category == ThoughtCategory.Agony) { interval *= 0.65f; }

            line.Category = category;
            line.Index = index;
            line.Severity = severity;
            line.CharInterval = interval;
            line.Delay = 0.12f;
            line.FadeIn = 0.22f;
            line.Hold = MathHelper.Clamp(1.5f + charCount * 0.05f, 1.8f, 4.5f);
            line.FadeOut = 0.75f;
            line.Elapsed = 0.0f;
            line.CharCount = charCount;
            line.Active = true;
            frame = default;
        }

        public void Update(float dt) {
            if (!line.Active) { return; }
            line.Advance(dt);
            frame = line.Evaluate();
            if (frame.Finished) {
                line.Active = false;
                frame = default;
            }
        }
    }
}
