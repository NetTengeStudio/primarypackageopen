using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Documents {
    public sealed class StampInteractionClientPlugin : PrimaryPackage.Core.IModule {
        private const double UseCooldownSeconds = 0.25;
                private static bool subsystemEnabled;
        private static readonly System.Random random = new System.Random();
        private static readonly ConditionalWeakTable<Item, StrongBox<double>> nextUseTime =
            new ConditionalWeakTable<Item, StrongBox<double>>();
        private static DocumentMarkComponent ForcedStampTarget;
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            subsystemEnabled = StampDefinitions.DetectSubsystemEnabled();
            if (!subsystemEnabled) { return; }
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Use),
                    new[] { typeof(float), typeof(Character), typeof(Limb), typeof(Entity), typeof(Character) }),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(StampInteractionClientPlugin), nameof(UsePostfix))));
            harmony.Patch(AccessTools.Method(typeof(Holdable), nameof(Holdable.Use)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StampDefinitions), nameof(StampDefinitions.HoldableUsePrefix))));
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.TryInteract)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StampInteractionClientPlugin), nameof(TryInteractPrefix))));
        }
        public static bool TryInteractPrefix(Item __instance, ref bool __result, Character user) {
            if (user == null || user != Character.Controlled) { return true; }
            var mark = __instance.GetComponent<DocumentMarkComponent>();
            if (mark == null) { return true; }
            Item heldStamp = user.HeldItems.FirstOrDefault(it => StampDefinitions.IsStampItem(it));
            if (heldStamp == null) { return true; }
            if (user.IsKeyHit(InputType.Select)) {
                if (mark.GetStamps().Count >= 5) { return true; }
                ForcedStampTarget = mark;
                heldStamp.Use(0.1f, user);
                __result = true;
                return false;
            }
            return true;
        }
        public void Release() {
        }
        public static void UsePostfix(Item __instance, Character user) {
            DocumentMarkComponent forcedTarget = ForcedStampTarget;
            ForcedStampTarget = null;
            if (!subsystemEnabled) { return; }
            if (!StampDefinitions.IsStampItem(__instance)) { return; }
            if (user == null || user != Character.Controlled) { return; }
            if (!PassCooldown(__instance)) { return; }
            DocumentMarkComponent target = forcedTarget ?? TryFindStampTarget(user);
            if (target == null) { return; }
            if (target.GetStamps().Count >= 5) { return; }
            Identifier stampId = StampDefinitions.GetStampIdFromItem(__instance);
            float occupiedTextBottom =
                PrimaryPackage.CoalitionDocument.CoalitionDocumentRenderer.GetOccupiedTextBottom(target.Item);
            Vector2 offset = StampPlacement.FindPlacement(target.GetStamps(), random, occupiedTextBottom);
            float rot = (float)(random.NextDouble() * 2.0 - 1.0) * StampPlacement.RotationRange;
            if (GameMain.Client != null) {
                SendStampRequest(target.Item, stampId, offset, rot);
            }
            else {
                target.ApplyStamp(stampId, offset, rot);
            }
        }
        private static bool PassCooldown(Item stampItem) {
            double now = Timing.TotalTime;
            StrongBox<double> slot = nextUseTime.GetValue(stampItem, _ => new StrongBox<double>(0.0));
            if (now < slot.Value) { return false; }
            slot.Value = now + UseCooldownSeconds;
            return true;
        }
        private static DocumentMarkComponent TryFindStampTarget(Character user) {
            Item focused = user.FocusedItem;
            if (focused != null && user.CanInteractWith(focused)) {
                var mark = focused.GetComponent<DocumentMarkComponent>();
                if (mark != null) { return mark; }
            }
            return user.Inventory?.AllItems
                .Select(item => item.GetComponent<DocumentMarkComponent>())
                .FirstOrDefault(mark => mark != null && mark.GetStamps().Count < 5);
        }
        private static void SendStampRequest(Item document, Identifier stampId, Vector2 offset, float rotation) {
            INet.Net.Send(StampDefinitions.ApplyNetId, msg => {
                msg.WriteUInt16(document.ID);
                msg.WriteIdentifier(stampId);
                msg.WriteSingle(offset.X);
                msg.WriteSingle(offset.Y);
                msg.WriteSingle(rotation);
            });
        }
    }
}
