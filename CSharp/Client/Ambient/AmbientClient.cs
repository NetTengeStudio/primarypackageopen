using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Economy;

namespace PrimaryPackage.Ambient {
    internal sealed class AmbientLayer {
        public string File;
        public float Volume = 1f;
        public bool Loop = true;
        public float MinInterval = 30f;
        public float MaxInterval = 60f;
        public Sound Sound;
        public SoundChannel Channel;
        public double NextPlayAt;
    }

    internal static class AmbientPlayer {
        private const string ConfigRelativePath = "Data/pp_ambient.xml";
        private const float FadeSpeed = 0.5f;

        private static readonly List<AmbientLayer> layers = new();
        private static float configVolume = 1f;
        private static bool loaded;

        private const string PrefsRelativePath = "Data/pp_ambient_client.xml";

        public static bool LocalEnabled = true;
        public static float LocalVolume = 1f;

        public static void LoadPrefs() {
            if (!PrimaryPackage.Core.ModAssets.Exists(PrefsRelativePath)) { return; }
            if (PrimaryPackage.Core.ModPrefs.Read(PrefsRelativePath, root => {
                LocalEnabled = (bool?)root.Attribute("enabled") ?? true;
                LocalVolume = MathHelper.Clamp((float?)root.Attribute("volume") ?? 1f, 0f, 1f);
            })) { return; }
            LocalEnabled = true;
            LocalVolume = 1f;
        }

        public static void SavePrefs() =>
            PrimaryPackage.Core.ModPrefs.Write(PrefsRelativePath, new XElement("AmbientPrefs",
                new XAttribute("enabled", LocalEnabled),
                new XAttribute("volume", LocalVolume)));
        public static bool ServerEnabled = true;
        public static float ServerVolume = 1f;
        private static bool stateRequested;

        public static void LoadConfig() {
            layers.Clear();
            loaded = false;
            PrimaryPackage.Core.ModPrefs.Read(ConfigRelativePath, root => {
                configVolume = MathHelper.Clamp((float?)root.Attribute("volume") ?? 1f, 0f, 1f);
                foreach (XElement el in root.Elements("Layer")) {
                    string file = (string)el.Attribute("file");
                    if (string.IsNullOrEmpty(file)) { continue; }
                    layers.Add(new AmbientLayer {
                        File = PrimaryPackage.Core.ModAssets.Resolve(file),
                        Volume = MathHelper.Clamp((float?)el.Attribute("volume") ?? 1f, 0f, 1f),
                        Loop = (bool?)el.Attribute("loop") ?? true,
                        MinInterval = (float?)el.Attribute("mininterval") ?? 30f,
                        MaxInterval = (float?)el.Attribute("maxinterval") ?? 60f
                    });
                }
            });
            LoadPrefs();
            loaded = true;
        }

        public static void Update(float deltaTime) {
            if (!loaded) { return; }
            RequestStateIfNeeded();
            bool shouldPlay = ShouldPlay();
            float master = configVolume * LocalVolume * ServerVolume;
            double now = Timing.TotalTime;
            foreach (AmbientLayer layer in layers) {
                float target = shouldPlay ? layer.Volume * master : 0f;
                if (layer.Loop) { UpdateLoop(layer, target, deltaTime); }
                else { UpdateOneShot(layer, target, now); }
            }
        }

        private static bool ShouldPlay() {
            return LocalEnabled && ServerEnabled
                && Screen.Selected == GameMain.GameScreen
                && GameMain.GameSession != null;
        }

        private static void UpdateLoop(AmbientLayer layer, float target, float deltaTime) {
            SoundChannel chn = layer.Channel;
            if (chn == null || !chn.IsPlaying) {
                if (target < 0.01f) { return; }
                layer.Sound ??= PrimaryPackage.Core.SoundStore.Absolute(layer.File);
                if (layer.Sound == null) { return; }
                chn = layer.Sound.Play(0.01f, SoundManager.SoundCategoryWaterAmbience);
                if (chn == null) { return; }
                chn.Looping = true;
                layer.Channel = chn;
            }
            chn.Gain += FadeSpeed * deltaTime * Math.Sign(target - chn.Gain);
            chn.Gain = MathHelper.Clamp(chn.Gain, 0f, Math.Max(target, 0.01f));
            if (target < 0.01f && chn.Gain <= 0.011f) {
                chn.FadeOutAndDispose();
                layer.Channel = null;
            }
        }

        private static void UpdateOneShot(AmbientLayer layer, float target, double now) {
            if (target < 0.01f) {
                layer.NextPlayAt = 0;
                return;
            }
            if (layer.Channel != null && layer.Channel.IsPlaying) { return; }
            layer.Channel = null;
            if (layer.NextPlayAt <= 0) {
                layer.NextPlayAt = now + Rand.Range(layer.MinInterval, layer.MaxInterval);
                return;
            }
            if (now < layer.NextPlayAt) { return; }
            layer.NextPlayAt = now + Rand.Range(layer.MinInterval, layer.MaxInterval);
            layer.Sound ??= PrimaryPackage.Core.SoundStore.Absolute(layer.File);
            layer.Channel = layer.Sound?.Play(target, SoundManager.SoundCategoryWaterAmbience);
        }

        public static void StopAll() {
            foreach (AmbientLayer layer in layers) {
                layer.Channel?.FadeOutAndDispose();
                layer.Channel = null;
            }
        }

        private static void RequestStateIfNeeded() {
            if (stateRequested) { return; }
            if (GameMain.Client == null) {
                ServerEnabled = true;
                ServerVolume = 1f;
                return;
            }
            stateRequested = true;
            PrimaryPackage.Core.ModNet.Send(AmbientNet.RequestId, msg => msg.WriteByte(1));
        }

        public static void ResetStateRequest() => stateRequested = false;

        public static void OnStateReceived(IReadMessage msg) {
            ServerEnabled = msg.ReadBoolean();
            ServerVolume = MathHelper.Clamp(msg.ReadSingle(), 0f, 1f);
        }
    }

    public sealed class AmbientClientPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.ILoadCompleted {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Method(typeof(SoundPlayer), nameof(SoundPlayer.Update)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(AmbientClientPlugin), nameof(SoundPlayerUpdatePostfix))));
            PrimaryPackage.Core.ModNet.Receive(AmbientNet.StateId, AmbientPlayer.OnStateReceived);
            PrimaryPackage.Core.ModCommands.Register("pp_ambient",
                "pp_ambient [on|off]: фоновые звуки станции локально", args => {
                    AmbientPlayer.LocalEnabled = args.Length > 0
                        ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                        : !AmbientPlayer.LocalEnabled;
                    AmbientPlayer.SavePrefs();
                    DebugConsole.NewMessage($"pp_ambient - {(AmbientPlayer.LocalEnabled ? "включён" : "выключен")}", Color.LightGreen);
                }, localOnClient: true, relayToServer: false);
            PrimaryPackage.Core.ModCommands.Register("pp_ambient_volume",
                "pp_ambient_volume [0..1]: громкость фоновых звуков", args => {
                    if (args.Length < 1 || !float.TryParse(args[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float value)) {
                        DebugConsole.NewMessage($"pp_ambient - громкость {AmbientPlayer.LocalVolume:0.##}", Color.White);
                        return;
                    }
                    AmbientPlayer.LocalVolume = MathHelper.Clamp(value, 0f, 1f);
                    AmbientPlayer.SavePrefs();
                    DebugConsole.NewMessage($"pp_ambient - громкость {AmbientPlayer.LocalVolume:0.##}", Color.LightGreen);
                }, localOnClient: true, relayToServer: false);
        }

        public void LoadCompleted() => AmbientPlayer.LoadConfig();

        public void Release() {
            AmbientPlayer.StopAll();
            AmbientPlayer.ResetStateRequest();
        }

        public static void SoundPlayerUpdatePostfix(float deltaTime) => AmbientPlayer.Update(deltaTime);
    }
}
