using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Lights;
using Barotrauma.Media;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using OpenAL;

namespace PrimaryPackage.VideoScreens {
    public partial class VideoScreen : IDrawableComponent {
        private const float RestartWindowSeconds = 1.5f;
        private const double LoadRetryCooldown = 5.0;
        private const double SyncRequestRetryInterval = 5.0;
        private const float VideoDepthOffset = 0.0005f;
        private const double AudioCheckInterval = 0.5;
        private const double AudioLagThreshold = 1.2;
        private const double AudioLagRestore = 0.3;
        private const double MuffleCheckInterval = 0.2;

        private Video video;
        private LightSource glowLight;
        private bool syncedOn;
        private bool hasSync;
        private double nextSyncRequestAt = double.NegativeInfinity;
        private double nextLoadAttemptAt = double.NegativeInfinity;
        private double localCycleStart = double.NaN;
        private float prevCycleElapsed;
        private double videoStartTime;
        private double nextAudioCheckAt;
        private long audioBaselineShorts = -1;
        private long audioDroppedShorts;
        private SoundChannel configuredChannel;
        private double nextMuffleCheckAt;

        private Vector2 VideoWorldSize {
            get {
                Vector2 size = new Vector2(ScreenWidth, ScreenHeight);
                if (item.Sprite is { } frame) {
                    size.X = Math.Min(size.X, frame.SourceRect.Width);
                    size.Y = Math.Min(size.Y, frame.SourceRect.Height);
                }
                return size * item.Scale;
            }
        }

        public Vector2 DrawSize => VideoWorldSize;

        private bool ShouldPlay => GameMain.Client == null ? EffectiveOn : hasSync && syncedOn;

        partial void OnItemLoadedProjSpecific() => RequestFullSyncIfNeeded();

        private void RequestFullSyncIfNeeded() {
            if (GameMain.Client == null || hasSync || Timing.TotalTime < nextSyncRequestAt) { return; }
            nextSyncRequestAt = Timing.TotalTime + SyncRequestRetryInterval;
            item.CreateClientEvent(this, new FullSyncRequestEventData());
        }

        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            syncedOn = msg.ReadBoolean();
            hasSync = true;
        }

        partial void UpdateProjSpecific(float deltaTime) {
            RequestFullSyncIfNeeded();
            if (!ShouldPlay) {
                StopPlayback();
                return;
            }
            float cullRange = Math.Max(SoundRange * 2f, 3000f);
            if (Vector2.DistanceSquared(GameMain.GameScreen.Cam.WorldViewCenter, item.WorldPosition) > cullRange * cullRange) {
                StopPlayback();
                return;
            }

            double now = Timing.TotalTime;
            if (double.IsNaN(localCycleStart)) {
                localCycleStart = now;
                prevCycleElapsed = 0f;
            }
            float elapsed = Wrap(now - localCycleStart, CycleLength);
            bool wrapped = elapsed < prevCycleElapsed;
            prevCycleElapsed = elapsed;

            if (elapsed >= Math.Max(0.1f, Duration)) {
                DisposeVideo();
                return;
            }
            if (wrapped && video != null) {
                DisposeVideo();
            }
            if (video == null) {
                if (elapsed > RestartWindowSeconds) {
                    localCycleStart = now;
                    prevCycleElapsed = 0f;
                }
                LoadVideo();
                if (video == null) { return; }
            }
            video.GetTexture();
            UpdateAudioChannel();
            UpdateAudioSync();
        }

        private void StopPlayback() {
            DisposeVideo();
            localCycleStart = double.NaN;
            prevCycleElapsed = 0f;
        }

        private void LoadVideo() {
            if (Timing.TotalTime < nextLoadAttemptAt) { return; }
            nextLoadAttemptAt = Timing.TotalTime + LoadRetryCooldown;
            string modDir = PrimaryPackage.Core.ModAssets.Root;
            if (string.IsNullOrEmpty(modDir) || string.IsNullOrEmpty(File)) { return; }
            string path = System.IO.Path.Combine(modDir, File);
            if (!Barotrauma.IO.File.Exists(path)) {
                DebugConsole.ThrowError("VideoScreen видеофайл не найден " + path);
                return;
            }
            try {
                video = Video.Load(GameMain.Instance.GraphicsDevice, GameMain.SoundManager, path);
            } catch (Exception e) {
                DebugConsole.ThrowError("VideoScreen не удалось загрузить видео " + path, e);
                video = null;
            }
            if (video == null) {
                DebugConsole.ThrowError("VideoScreen не удалось декодировать видео " + path);
                return;
            }
            nextLoadAttemptAt = double.NegativeInfinity;
            videoStartTime = Timing.TotalTime;
            nextAudioCheckAt = videoStartTime + 1.0;
            audioBaselineShorts = -1;
            audioDroppedShorts = 0;
            video.AudioGain = MathHelper.Clamp(Volume, 0f, 1f);
            CreateGlowLight();
        }

        private void CreateGlowLight() {
            if (video.GetTexture() is not { } texture) { return; }
            Vector2 worldSize = VideoWorldSize;
            glowLight = new LightSource(item.Position, 0f, Color.White, item.Submarine) {
                CastShadows = false,
                Rotation = -item.RotationRad,
                SpriteScale = new Vector2(worldSize.X / texture.Width, worldSize.Y / texture.Height)
            };
            glowLight.LightSourceParams.OverrideLightSpriteAlpha = 1f;
            glowLight.LightSourceParams.LightSprite = PrimaryPackage.Core.TextureStore.Borrow(
                texture, null, new Vector2(texture.Width / 2f, texture.Height / 2f));
        }

        private void UpdateAudioChannel() {
            if (video.sound?.soundChannel is not { } channel) { return; }
            if (!ReferenceEquals(channel, configuredChannel)) {
                configuredChannel = channel;
                channel.Category = SoundManager.SoundCategoryDefault;
                channel.Near = SoundRange * 0.4f;
                channel.Far = SoundRange;
                channel.Gain = MathHelper.Clamp(Volume, 0f, 1f);
                nextMuffleCheckAt = 0.0;
            }
            channel.Position = new Vector3(item.WorldPosition, 0.0f);
            float gain = MathHelper.Clamp(Volume, 0f, 1f);
            if (channel.Gain != gain) {
                channel.Gain = gain;
                video.AudioGain = gain;
            }
            if (Timing.TotalTime >= nextMuffleCheckAt) {
                nextMuffleCheckAt = Timing.TotalTime + MuffleCheckInterval;
                channel.Muffled = SoundPlayer.ShouldMuffleSound(
                    Character.Controlled, item.WorldPosition, SoundRange, Character.Controlled?.CurrentHull);
            }
        }

        private void UpdateAudioSync() {
            if (Timing.TotalTime < nextAudioCheckAt || video.sound is not { } snd) { return; }
            nextAudioCheckAt = Timing.TotalTime + AudioCheckInterval;
            int shortsPerSecond = snd.SampleRate * (snd.ALFormat == Al.FormatStereo16 ? 2 : 1);
            if (shortsPerSecond <= 0) { return; }
            long remaining = 0;
            lock (snd.mutex) {
                foreach (short[] chunk in snd.sampleQueue) { remaining += chunk.Length; }
            }
            if (audioBaselineShorts < 0) {
                audioBaselineShorts = remaining + (long)((Timing.TotalTime - videoStartTime) * shortsPerSecond);
                return;
            }
            double consumedSeconds = (double)(audioBaselineShorts - remaining - audioDroppedShorts) / shortsPerSecond;
            double behind = Timing.TotalTime - videoStartTime - consumedSeconds;
            if (behind <= AudioLagThreshold) { return; }
            long toDrop = (long)((behind - AudioLagRestore) * shortsPerSecond);
            lock (snd.mutex) {
                while (toDrop > 0 && snd.sampleQueue.Count > 0) {
                    short[] head = snd.sampleQueue.Peek();
                    if (head.Length > toDrop) { break; }
                    snd.sampleQueue.Dequeue();
                    toDrop -= head.Length;
                    audioDroppedShorts += head.Length;
                }
            }
        }

        private void DisposeVideo() {
            if (glowLight != null) {
                PrimaryPackage.Core.TextureStore.Detach(glowLight.LightSourceParams.LightSprite);
                glowLight.LightSourceParams.LightSprite = null;
                glowLight.Remove();
                glowLight = null;
            }
            video?.Dispose();
            video = null;
            configuredChannel = null;
            nextLoadAttemptAt = double.NegativeInfinity;
        }

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (video?.GetTexture() is not { } texture) { return; }
            Vector2 drawPos = new Vector2(item.DrawPosition.X, -item.DrawPosition.Y);
            Vector2 worldSize = VideoWorldSize;
            Vector2 scale = new Vector2(worldSize.X / texture.Width, worldSize.Y / texture.Height);
            float depth = MathHelper.Clamp(itemDepth - VideoDepthOffset, 0f, 1f);
            spriteBatch.Draw(texture, drawPos, null, overrideColor ?? Color.White, item.RotationRad,
                new Vector2(texture.Width / 2f, texture.Height / 2f), scale, SpriteEffects.None, depth);
        }

        public override void RemoveComponentSpecific() {
            base.RemoveComponentSpecific();
            DisposeVideo();
        }
    }
}
