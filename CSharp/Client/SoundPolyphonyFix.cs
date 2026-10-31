using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Sounds;
using HarmonyLib;
using OpenAL;
namespace SoundPolyphony {
    public sealed class SoundPolyphonyPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
        public void Release() {
            SoundPriorityRegistry.Clear();
        }
    }
    [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.CountPlayingInstances))]
    public static class PolyphonyCeilingPatch {
        private const int MinSimultaneousInstances = 16;
        public static void Postfix(Sound sound, ref int __result) {
            int ceiling = Math.Max(sound.MaxSimultaneousInstances, MinSimultaneousInstances);
            __result = __result >= ceiling ? ceiling : 0;
        }
    }
    [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.AssignFreeSourceToChannel))]
    public static class PriorityVoiceStealingPatch {
        public static bool Prefix(SoundManager __instance, SoundChannel newChannel, ref int __result) {
            if (__instance.Disabled) { __result = -1; return false; }
            int poolIndex = (int)newChannel.Sound.SourcePoolIndex;
            var pool = __instance.playingChannels[poolIndex];
            lock (pool) {
                for (int i = 0; i < pool.Length; i++) {
                    if (pool[i] == null || !pool[i].IsPlaying) {
                        if (pool[i] != null && !TryDispose(pool[i])) { continue; }
                        pool[i] = newChannel;
                        __result = i;
                        return false;
                    }
                }
                if (newChannel.IsStream) {
                    __result = -1;
                    return false;
                }
                int incomingPriority = SoundPriorityRegistry.Resolve(newChannel);
                int minPriority = int.MaxValue, minIndex = -1;
                float minScore = float.MaxValue;
                int sameTierCount = 0, sameTierIndex = -1;
                float sameTierScore = float.MaxValue;
                for (int i = 0; i < pool.Length; i++) {
                    SoundChannel c = pool[i];
                    if (c == null || c.IsStream || c.FadingOutAndDisposing) { continue; }
                    int pr = SoundPriorityRegistry.Resolve(c);
                    float score = StealScore(c);
                    if (pr < minPriority || (pr == minPriority && score < minScore)) {
                        minPriority = pr;
                        minIndex = i;
                        minScore = score;
                    }
                    if (pr == incomingPriority) {
                        sameTierCount++;
                        if (score < sameTierScore) {
                            sameTierScore = score;
                            sameTierIndex = i;
                        }
                    }
                }
                int cap = TierCap(incomingPriority, pool.Length);
                if (cap > 0 && sameTierCount >= cap && sameTierIndex >= 0) {
                    if (TryDispose(pool[sameTierIndex])) {
                        pool[sameTierIndex] = newChannel;
                        __result = sameTierIndex;
                        return false;
                    }
                }
                if (minIndex >= 0 && minPriority < incomingPriority) {
                    if (TryDispose(pool[minIndex])) {
                        pool[minIndex] = newChannel;
                        __result = minIndex;
                        return false;
                    }
                }
                if (sameTierIndex >= 0) {
                    if (TryDispose(pool[sameTierIndex])) {
                        pool[sameTierIndex] = newChannel;
                        __result = sameTierIndex;
                        return false;
                    }
                }
                __result = -1;
                return false;
            }
        }
        private static float StealScore(SoundChannel channel) {
            float score = channel.Gain;
            if (channel.Muffled) { score *= 0.5f; }
            return score;
        }

        private static bool TryDispose(SoundChannel channel) {
            Al.GetError();
            try {
                channel.Dispose();
                return true;
            }
            catch {
                Al.GetError();
                return false;
            }
        }
        private static int TierCap(int priority, int poolLength) {
            if (priority == SoundPriority.Ui) { return Math.Max(1, poolLength * 5 / 16); }
            if (priority == SoundPriority.Background) { return Math.Max(1, poolLength * 3 / 16); }
            return 0;
        }
    }
}
