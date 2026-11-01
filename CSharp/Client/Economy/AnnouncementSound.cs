using System;
using System.IO;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using HarmonyLib;
using SoundPolyphony;
namespace PrimaryPackage.Economy {
    public sealed class AnnouncementSoundPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            var target = AccessTools.Method(typeof(GameClient), nameof(GameClient.AddChatMessage), new[] { typeof(ChatMessage) });
            if (target == null) { return; }
            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(AnnouncementSound), nameof(AnnouncementSound.OnChatMessage))));
        }
        public void Release() {
            AnnouncementSound.Dispose();
        }
    }
    public static class AnnouncementSound {
        private const string SoundFile = "Content/Sounds/UI/announce.ogg";
        private const string FallbackMarker = "ГРОМКОГОВОРИТЕЛИ";
        private static string marker;
        private static double lastPlayTime = double.NegativeInfinity;
        public static void OnChatMessage(ChatMessage message) {
            if (message == null) { return; }
            marker ??= OrderText.Get("pp.order.announce.channel", FallbackMarker).Trim('[', ']', ' ');
            if (marker.Length == 0) { marker = FallbackMarker; }
            bool matched =
                (message.SenderName != null && message.SenderName.IndexOf(marker, StringComparison.Ordinal) >= 0) ||
                (message.Text != null && message.Text.IndexOf(marker, StringComparison.Ordinal) >= 0);
            if (!matched) { return; }
            double now = Timing.TotalTime;
            if (now - lastPlayTime < 0.5) { return; }
            lastPlayTime = now;
            Resolve()?.Play(0.7f);
        }
        private static Sound Resolve() {
            string p = PrimaryPackage.Core.ModAssets.Resolve(SoundFile);
            if (p.Length == 0 || !File.Exists(p)) { return null; }
            Sound s = PrimaryPackage.Core.SoundStore.Absolute(p);
            if (s != null) { SoundPriorityRegistry.Register(p, SoundPriority.Critical); }
            return s;
        }
        public static void Dispose() {
            lastPlayTime = double.NegativeInfinity;
        }
    }
}
