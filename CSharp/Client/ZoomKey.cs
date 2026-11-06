using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
namespace PrimaryPackage.ZoomKey {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public sealed class ZoomKeyPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            var target = AccessTools.Method(typeof(Camera), nameof(Camera.MoveCamera));
            if (target == null || !PrimaryPackage.Core.ViewState.Supported) { return; }
            harmony.Patch(target, prefix: HarmonyPatchOrder.Ordered(
                AccessTools.Method(typeof(CustomZoomPatch), nameof(CustomZoomPatch.Prefix)), HarmonyPatchOrder.ZoomKeyOrder));
            PrimaryPackage.Core.HudFrame.Over(PrimaryPackage.Core.HudFrame.Zoom, ImmersionZoom.Draw);
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ImmersionZoom), nameof(ImmersionZoom.LockMovement))));
        }
        public void Release() {
            ImmersionZoom.Cancel();
            PrimaryPackage.Core.ViewState.Release();
        }
    }
    internal static class CustomZoomPatch {
        private const float ZoomSpeedMultiplier = 0.6f, ScrollWheelDivisor = 1000.0f, KeyZoomStep = 0.3f;

        public static bool Prefix(Camera __instance, float deltaTime) {
            if (Screen.Selected != GameMain.GameScreen || __instance.TargetPos == Vector2.Zero) {
                ImmersionZoom.Cancel();
                return true;
            }
            ManualZoom();
            ImmersionZoom.Update(__instance, deltaTime);
            PrimaryPackage.Core.ViewState.Apply(__instance);
            return true;
        }
        private static void ManualZoom() {
            bool keyboardFocused = PrimaryPackage.Core.PlayView.Typing;
            bool mouseOnUi = PrimaryPackage.GUI.GUIBridge.CapturesViewInput;
            int scrollDelta = mouseOnUi ? 0 : PlayerInput.ScrollWheelSpeed;
            bool zoomInHit = !keyboardFocused && !mouseOnUi && PrimaryPackage.Core.ModSettings.ZoomInKey.Hit();
            bool zoomOutHit = !keyboardFocused && !mouseOnUi && PrimaryPackage.Core.ModSettings.ZoomOutKey.Hit();
            if (!keyboardFocused && !mouseOnUi && PrimaryPackage.Core.ModSettings.ZoomResetKey.Hit()) {
                PrimaryPackage.Core.ViewState.Bias = 0f;
                return;
            }
            float span = PrimaryPackage.Core.ViewState.Ceiling - PrimaryPackage.Core.ViewState.Floor;
            float delta = (zoomInHit ? KeyZoomStep : 0f) - (zoomOutHit ? KeyZoomStep : 0f)
                + (scrollDelta != 0 ? scrollDelta / ScrollWheelDivisor * span * ZoomSpeedMultiplier : 0f);
            if (delta != 0f) { PrimaryPackage.Core.ViewState.Bias += delta; }
        }
    }
    internal static class ImmersionZoom {
        private const float ChargeSeconds = 2.0f;
        private const float EntrySmoothTime = 0.55f;
        private const float ExitSmoothTime = 0.22f;
        private const float ZoomOutFactor = 0.5f;
        private const float FocusDistance = 700f;
        private const float VignetteStrength = 0.7f;
        private const float FadeInTime = 0.18f, FadeOutTime = 0.3f;
        private const float Eps = 0.0015f;
        private const int VignetteRes = 256;
        private const float VignetteInner = 0.62f;
        private sealed class State {
            public bool PrevRmb, PressValid, Engaged;
            public float Charge, Factor, FactorVel;
        }
        private static readonly ConditionalWeakTable<Camera, State> States = new();
        private static float barCharge, barFactor, barAlpha, barAlphaVel;
        private static bool effectiveHeld;
        private const string VignetteKey = "zoomkey/vignette";
        internal static void Cancel() {
            barCharge = 0f; barFactor = 0f; barAlpha = 0f; barAlphaVel = 0f; effectiveHeld = false;
            PrimaryPackage.Core.ViewState.Contribute(PrimaryPackage.Core.ViewChannel.Immersion, 1.0f);
        }
        private static bool Eligible() {
            if (Screen.Selected != GameMain.GameScreen || PrimaryPackage.Core.PlayView.Typing) { return false; }

            Character c = Character.Controlled;
            if (c?.Inventory == null || c.IsDead || c.IsUnconscious || c.InWater) { return false; }
            return c.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) == null && c.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) == null;
        }
        private static float SmoothDamp(float current, float target, ref float vel, float smoothTime, float dt) {
            float omega = 2f / smoothTime;
            float x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float temp = (vel + omega * change) * dt;
            vel = (vel - omega * temp) * exp;
            return target + (change + temp) * exp;
        }
        internal static void Update(Camera cam, float deltaTime) {
            bool eligible = Eligible();
            bool capturesView = PrimaryPackage.GUI.GUIBridge.CapturesViewInput;
            bool rmb = PlayerInput.SecondaryMouseButtonHeld();
            State st = States.GetOrCreateValue(cam);
            bool pressEdge = rmb && !st.PrevRmb;
            if (pressEdge) { st.PressValid = eligible && !capturesView; }
            if (!rmb) { st.PressValid = false; }
            bool held = rmb && st.PressValid && eligible;
            st.PrevRmb = rmb;
            effectiveHeld = held;
            // Hover suspends an existing look gesture; it neither cancels it nor
            // starts/charges one from a press that belongs to the GUI.
            float step = capturesView && held ? 0f : deltaTime;
            st.Charge = MathHelper.Clamp(st.Charge + (held ? step : -step) / ChargeSeconds, 0f, 1f);
            if (!held) { st.Engaged = false; }
            else if (st.Charge >= 1f) { st.Engaged = true; }
            st.Factor = MathHelper.Clamp(SmoothDamp(st.Factor, st.Engaged ? 1f : 0f, ref st.FactorVel,
                st.Engaged ? EntrySmoothTime : ExitSmoothTime, step), 0f, 1f);
            barCharge = st.Charge;
            barFactor = st.Factor;
            barAlpha = MathHelper.Clamp(SmoothDamp(barAlpha, held ? 1f : 0f, ref barAlphaVel, held ? FadeInTime : FadeOutTime, step), 0f, 1f);
            PrimaryPackage.Core.ViewState.Contribute(PrimaryPackage.Core.ViewChannel.Immersion,
                MathHelper.Lerp(1.0f, ZoomOutFactor, st.Factor));
            if (st.Factor > Eps) {
                cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, FocusDistance, st.Factor);
            }
        }
        internal static void LockMovement(Character __instance) {
            if (__instance != Character.Controlled || !effectiveHeld) { return; }
            __instance.ClearInput(InputType.Left);
            __instance.ClearInput(InputType.Right);
            __instance.ClearInput(InputType.Up);
            __instance.ClearInput(InputType.Down);
        }
        private static Texture2D Vignette() =>
            PrimaryPackage.Core.TextureStore.Generated(VignetteKey, VignetteRes, static resolution => {
                Color[] data = new Color[resolution * resolution];
                float c = (resolution - 1) * 0.5f;
                float maxD = new Vector2(c, c).Length();
                for (int y = 0; y < resolution; y++) {
                    for (int xp = 0; xp < resolution; xp++) {
                        float d = new Vector2(xp - c, y - c).Length() / maxD;
                        float a = MathHelper.Clamp((d - VignetteInner) / (1f - VignetteInner), 0f, 1f);
                        a = a * a * (3f - 2f * a);
                        data[y * resolution + xp] = new Color((byte)0, (byte)0, (byte)0, (byte)(a * 255f));
                    }
                }
                Texture2D created = new Texture2D(GameMain.Instance.GraphicsDevice, resolution, resolution);
                created.SetData(data);
                return created;
            });
        internal static void Draw(Camera cam, SpriteBatch spriteBatch) {
            if (spriteBatch == null || (barAlpha <= 0.01f && barFactor <= Eps) || Screen.Selected != GameMain.GameScreen || GUI.PauseMenuOpen || Character.Controlled == null) { return; }
            try {
                if (barFactor > Eps) {
                    spriteBatch.Draw(Vignette(), new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                        Color.White * MathHelper.Clamp(barFactor * VignetteStrength, 0f, 1f));
                }
                if (barAlpha <= 0.01f) { return; }
                int width = GUI.IntScale(150), height = GUI.IntScale(22), border = GUI.IntScale(3);
                float slide = (1f - barAlpha) * GUI.IntScale(10);
                Vector2 pos = new Vector2((GameMain.GraphicsWidth - width) * 0.5f, HUDLayoutSettings.InventoryAreaLower.Y - GUI.IntScale(30) - height + slide);
                Vector2 size = new Vector2(width, height);
                GUI.DrawRectangle(spriteBatch, pos, size, Color.Black * (0.4f * barAlpha), isFilled: true);
                float inset = border + 1f;
                float fill = MathHelper.Clamp(barCharge, 0f, 1f) * (width - inset * 2f);
                if (fill > 1f) { GUI.DrawRectangle(spriteBatch, pos + new Vector2(inset, inset), new Vector2(fill, height - inset * 2f), Color.White * (0.55f * barAlpha), isFilled: true); }
                GUI.DrawRectangle(spriteBatch, pos, size, Color.White * (0.7f * barAlpha), isFilled: false, depth: 0f, thickness: border);
            } catch (InvalidOperationException) { }
        }
    }
}
