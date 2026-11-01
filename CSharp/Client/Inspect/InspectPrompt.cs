using System;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Inspect {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public static class InspectPrompt {
        private const float BoxPadX = 7.0f;
        private const float BoxPadY = 3.0f;
        private const float BoxDepth = 0.001f;
        private const float TextDepth = 0.0f;
        private const float Lift = 38.0f;
        private const float FollowRate = 16.0f;
        private const float SnapDistance = 800.0f;
        private const float RingAppear = 0.16f;
        private const float RingVanish = 0.26f;
        private const float RingRadius = 32.0f;
        private const float RingThickness = 3.0f;
        private const float RingMinScale = 0.72f;
        private const double MaintainInterval = 1.0;

        private static readonly Color RingTrackColor = new Color(16, 20, 26);

        private static InspectTarget current;
        private static InspectTarget held;
        private static string text = "";
        private static string cachedRaw;
        private static Vector2 textSize;
        private static float cachedGuiScale = -1.0f;
        private static LanguageIdentifier cachedLanguage;
        private static InspectAnim fade, hold, ring;
        private static Vector2 shownWorld;
        private static bool placed;
        private static bool appearing;
        private static bool holding;
        private static bool useLatched;
        private static bool useDown;
        private static double nextMaintain;
        private static float ringLift;

        internal static bool Diag => PrimaryPackage.Core.ModSettings.InspectDebug.Value;
        internal static bool InputPatched;

        internal static void LatchUse() {
            if (PrimaryPackage.Core.PlayView.Typing) { return; }
            if (PlayerInput.KeyHit(InputType.Use)) { useLatched = true; }
        }

        private static bool ConsumeUse() {
            bool down = !PrimaryPackage.Core.PlayView.Typing && PlayerInput.KeyDown(InputType.Use);
            bool edge = down && !useDown;
            useDown = down;
            if (!InputPatched) { return edge; }
            bool latched = useLatched;
            useLatched = false;
            return latched;
        }

        internal static void Draw(Camera cam, SpriteBatch spriteBatch) {
            InspectClock.Tick();
            Maintain();
            InspectScanner.Update();
            InspectWorldText.Tick();
            InspectRuntime.Sync();
            InspectTarget want = Resolve();
            UpdateHold(want);
            UpdateFade(want);
            if (Diag) { DrawDiag(spriteBatch, want); }
            RenderRing(cam, spriteBatch);
            RenderPrompt(cam, spriteBatch);
        }

        private static void Maintain() {
            double now = Timing.TotalTime;
            if (now < nextMaintain) { return; }
            nextMaintain = now + MaintainInterval;
            InspectRegistry.Prune();
            if (GameMain.GameSession == null) { InspectSeen.Clear(); }
            else { InspectSeen.Prune(); }
        }

        private static InspectTarget Resolve() {
            Character character = Character.Controlled;
            if (character == null || Screen.Selected != GameMain.GameScreen) { return default; }
            if (character.IsIncapacitated || character.Stun > 0.0f || !character.CanInteract) { return default; }
            if (CharacterHUD.IsCampaignInterfaceOpen || CharacterHealth.OpenHealthWindow != null) { return default; }
            if (GUI.DisableHUD || Inventory.DraggingItemToWorld) { return default; }

            InspectTarget scanned = InspectScanner.Hovered;
            if (scanned.Valid) { return scanned; }

            Item item = character.FocusedItem;
            if (item == null || item.Removed) { return default; }
            InspectTarget focused = InspectSource.For(item);
            return focused.Valid && focused.Settings.PromptEnabled && !InspectSeen.Blocks(focused.Settings, item)
                ? focused : default;
        }

        private static void UpdateHold(in InspectTarget want) {
            bool down = !PrimaryPackage.Core.PlayView.Typing && PlayerInput.KeyDown(InputType.Use);
            if (ConsumeUse() && want.Valid) {
                if (InspectRuntime.IsPlaying(want) || want.Settings.HoldSeconds <= 0.0f) {
                    InspectRuntime.Activate(want);
                } else {
                    held = want;
                    hold.Reset();
                    holding = true;
                }
            }
            if (holding && (!down || !want.Valid || !want.Same(held) || !held.Valid)) { holding = false; }
            if (holding) {
                ring.Advance(true, InspectClock.Step, RingAppear);
                if (hold.Advance(true, InspectClock.Step, held.Settings.HoldSeconds)) {
                    holding = false;
                    InspectRuntime.Activate(held);
                }
                return;
            }
            if (ring.Advance(false, InspectClock.Step, RingVanish)) {
                hold.Reset();
                held = default;
            }
        }

        private static void UpdateFade(in InspectTarget want) {
            if (want.Valid && !want.Same(current)) { Adopt(want); }
            if (!current.Valid) {
                current = default;
                fade.Reset();
                placed = false;
                return;
            }
            appearing = want.Valid && !InspectRuntime.AnyPlaying;
            float duration = (appearing ? current.Settings.PromptFadeIn : current.Settings.PromptFadeOut) * 0.001f;
            if (fade.Advance(appearing, InspectClock.Step, duration) && !appearing) {
                current = default;
                placed = false;
            }
        }

        private static void Adopt(in InspectTarget target) {
            current = target;
            Rebuild();
        }

        private static void Rebuild() {
            string raw = current.Settings.PromptText ?? "";
            LocalizedString localized = TextManager.Get(raw);
            text = TextManager.ParseInputTypes(localized.Loaded ? localized : raw).Value;
            textSize = GUIStyle.SubHeadingFont.MeasureString(text);
            cachedRaw = raw;
            cachedGuiScale = GUI.Scale;
            cachedLanguage = GameSettings.CurrentConfig.Language;
        }

        private static Vector2 DrawAnchor(MapEntity entity) {
            if (entity is Item item && item.body is { Enabled: true } body) {
                body.UpdateDrawPosition();
                return body.DrawPosition;
            }
            return entity.DrawPosition;
        }

        private static void RenderPrompt(Camera cam, SpriteBatch spriteBatch) {
            if (fade.Idle || !current.Valid) { return; }
            IInspectSettings settings = current.Settings;
            if (cachedGuiScale != GUI.Scale || cachedLanguage != GameSettings.CurrentConfig.Language ||
                !string.Equals(cachedRaw, settings.PromptText ?? "", StringComparison.Ordinal)) { Rebuild(); }
            if (text.Length == 0) { return; }

            float eased = ToastTimeline.Ease(appearing ? settings.PromptEasingIn : settings.PromptEasingOut, fade.Value);
            float scale = settings.PromptScaleIn ? 0.94f + 0.06f * eased : 1.0f;
            MapEntity entity = current.Entity;
            Vector2 world = DrawAnchor(entity);
            float lift = Lift * GUI.Scale;
            if (ringLift > 0.0f && held.Valid && ReferenceEquals(held.Entity, entity)) { lift += ringLift; }

            if (!placed || Vector2.DistanceSquared(shownWorld, world) > SnapDistance * SnapDistance) {
                shownWorld = world;
                placed = true;
            } else {
                shownWorld += (world - shownWorld) * (1.0f - (float)Math.Exp(-FollowRate * InspectClock.Step));
            }

            Vector2 box = textSize * scale + new Vector2(BoxPadX * 2.0f, BoxPadY * 2.0f);
            Vector2 anchor = cam.WorldToScreen(shownWorld);
            anchor.Y -= lift + box.Y * 0.5f + settings.PromptOffsetY + settings.PromptRise * (1.0f - eased);

            ToastRenderer.DrawBox(spriteBatch, anchor - box * 0.5f, box, Color.Black * (0.7f * eased),
                Color.Transparent, BoxDepth, 1.0f);
            GUIStyle.SubHeadingFont.DrawString(spriteBatch, text, anchor, settings.PromptColor * eased,
                0.0f, textSize * 0.5f, Vector2.One * scale, SpriteEffects.None, TextDepth);
        }

        private static void RenderRing(Camera cam, SpriteBatch spriteBatch) {
            ringLift = 0.0f;
            if (ring.Idle || !held.Valid) { return; }
            float appear = ToastTimeline.Ease(holding ? ZoneEasing.Торможение : ZoneEasing.Разгон, ring.Value);
            float fill = ToastTimeline.Ease(ZoneEasing.Плавно, hold.Value);
            float scale = (RingMinScale + (1.0f - RingMinScale) * appear) * GUI.Scale;
            float radius = RingRadius * scale;
            ringLift = radius * appear;
            InspectRing.DrawGauge(spriteBatch, cam.WorldToScreen(DrawAnchor(held.Entity)), radius,
                RingThickness * scale, fill, RingTrackColor * (0.7f * appear),
                Color.Lerp(held.Settings.PromptColor, Color.White, fill * fill) * appear, BoxDepth);
        }

        private static void DrawDiag(SpriteBatch spriteBatch, in InspectTarget want) {
            Character character = Character.Controlled;
            string line1 =
                $"patch={InputPatched} canInteract={(character != null && character.CanInteract)} " +
                $"scanned={InspectScanner.Hovered.Valid} focused={(character?.FocusedItem?.Name ?? "-")} " +
                $"seen={InspectSeen.Count}";
            string line2 =
                $"want={(want.Valid ? want.Entity.Name : "-")} playing={InspectRuntime.AnyPlaying} " +
                $"hold={hold.Value:F2} ring={ring.Value:F2} fade={fade.Value:F2} " +
                $"light={(want.Valid ? InspectLight.Level(want) : -1.0f):F2}";
            GUIStyle.SmallFont.DrawString(spriteBatch, line1, new Vector2(14.0f, 14.0f), Color.Yellow);
            GUIStyle.SmallFont.DrawString(spriteBatch, line2, new Vector2(14.0f, 32.0f), Color.Yellow);
            GUIStyle.SmallFont.DrawString(spriteBatch, ScriptToast.Diag(), new Vector2(14.0f, 50.0f), Color.Orange);
            GUIStyle.SmallFont.DrawString(spriteBatch, InspectWorldText.Diag(), new Vector2(14.0f, 68.0f), Color.Orange);
        }
    }

    public sealed class InspectPromptPlugin : PrimaryPackage.Core.IModule {
        private Harmony harmony;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            PrimaryPackage.Core.HudFrame.Over(PrimaryPackage.Core.HudFrame.Prompt, GuiDrawPostfix);
            InspectPrompt.InputPatched = Patch(
                AccessTools.Method(typeof(PlayerInput), nameof(PlayerInput.Update), new[] { typeof(double) }),
                nameof(PlayerInputPostfix));
            Patch(AccessTools.Method(typeof(Item), nameof(Item.Draw), new[] { typeof(SpriteBatch), typeof(bool), typeof(bool) }),
                nameof(EntityDrawPostfix));
            Patch(AccessTools.Method(typeof(Structure), nameof(Structure.Draw), new[] { typeof(SpriteBatch), typeof(bool), typeof(bool) }),
                nameof(EntityDrawPostfix));
        }

        private bool Patch(System.Reflection.MethodBase target, string postfix) {
            if (target == null) { return false; }
            try {
                harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(InspectPromptPlugin), postfix)));
                return true;
            }
            catch (System.Exception) { return false; }
        }


        public void Release() {
        }

        public static void GuiDrawPostfix(Camera cam, SpriteBatch spriteBatch) => InspectPrompt.Draw(cam, spriteBatch);

        public static void PlayerInputPostfix() => InspectPrompt.LatchUse();

        public static void EntityDrawPostfix(MapEntity __instance, SpriteBatch spriteBatch, bool editing) =>
            InspectWorldText.DrawEntity(spriteBatch, __instance, editing);
    }
}
