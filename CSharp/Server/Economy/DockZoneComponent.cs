using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Economy {
    public partial class DockZoneComponent {
        private const double CheckInterval = 1.0;
        private const double ApproachDelay = 1.0;
        private const double DepartDelay = 1.0;
        private const double JobCooldown = 8.0;
        private const double ReasonRepeatSeconds = 5.0;
        private const float ReasonRange = 800.0f;
        private static readonly Identifier OrderCratePrefabId = "pp_ordercrate".ToIdentifier();
        private enum Phase { Idle, Approaching, Departing }
        private Phase phase = Phase.Idle;
        private double nextCheckTime;
        private double phaseUntil;
        private double cooldownUntil;
        private string lastReason;
        private double lastReasonTime;
        private Item jobCrate;
        private OrderRegistry.DeliveryClaim jobClaim;
        private OrderSet jobSet;
        private OrderRegistry.PendingOrder jobIncoming;
        private const double ApproachLeadSeconds = 30.0;
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            double now = Timing.TotalTime;
            if (OrderRegistry.ShouldTickExpiry(now)) {
                int distanceMeters = FarthestHullDistanceMeters();
                foreach (OrderRegistry.DeliveryClaim approaching in OrderRegistry.GetClaimsEnteringApproach(now, ApproachLeadSeconds)) {
                    OrderAnnounce.ShuttleApproach(approaching.Callsign, distanceMeters, (int)ApproachLeadSeconds);
                }
                foreach (OrderRegistry.DeliveryClaim expired in OrderRegistry.TakeExpiredClaims(now)) {
                    FailExpiredClaim(expired);
                }
            }
            if (phase == Phase.Approaching) {
                if (now >= phaseUntil) { CompleteSequence(now); }
                return;
            }
            if (phase == Phase.Departing) {
                if (now >= phaseUntil) {
                    SendCue(CueUndock);
                    phase = Phase.Idle;
                    cooldownUntil = now + JobCooldown;
                }
                return;
            }
            if (now < nextCheckTime) { return; }
            nextCheckTime = now + CheckInterval;
            if (now < cooldownUntil) { return; }
            TryBeginSequence(now);
        }
        private void FailExpiredClaim(OrderRegistry.DeliveryClaim claim) {
            int penalty = OrderRegistry.ApplyFailurePenalty(claim);
            HintSender.SendNear(item.WorldPosition, ReasonRange,
                penalty > 0
                    ? OrderText.Format("pp.order.expired.penalty",
                        $"Наряд №{claim.OrderNumber} провален груз не доставлен вовремя. Штраф {penalty:N0} кр.",
                        ("[num]", claim.OrderNumber.ToString()), ("[penalty]", penalty.ToString("N0")))
                    : OrderText.Format("pp.order.expired.nopenalty",
                        $"Наряд №{claim.OrderNumber} провален груз не доставлен вовремя.",
                        ("[num]", claim.OrderNumber.ToString())),
                HintToastNet.SeverityError);
        }
        private int FarthestHullDistanceMeters() {
            float maxDist = 0f;
            Vector2 port = item.WorldPosition;
            foreach (Hull h in Hull.HullList) {
                if (item.Submarine != null && h.Submarine != item.Submarine) { continue; }
                float d = Vector2.Distance(port, h.WorldPosition);
                if (d > maxDist) { maxDist = d; }
            }
            return System.Math.Max(1, (int)System.Math.Round(maxDist / 100.0));
        }
        private void TryBeginSequence(double now) {
            Hull hull = item.CurrentHull;
            if (hull == null) { return; }
            bool hasDelivery = TryFindDeliveryJob(hull, out Item crate, out OrderRegistry.DeliveryClaim claim,
                out OrderSet deliverySet, out string deliveryReason);
            OrderRegistry.PendingOrder incoming = null;
            OrderSet incomingSet = null;
            if (!hasDelivery) {
                incoming = OrderRegistry.PeekNextApproved();
                if (incoming != null && !OrderSets.TryGet(incoming.OrderSetId, out incomingSet)) {
                    SendReason(now, OrderText.Format("pp.order.dock.set_missing",
                        $"Комплект закупки №{incoming.Id} больше не существует.", ("[num]", incoming.Id.ToString())));
                    return;
                }
            }
            if (!hasDelivery && incoming == null) {
                if (deliveryReason != null) { SendReason(now, deliveryReason); }
                return;
            }
            if (OrderRegistry.IsZoneLocked(item.ID)) { SendReason(now, OrderText.Get("pp.order.dock.zone_locked", "Стыковочная зона заблокирована ГМ.")); return; }
            if (!AllLinkedDoorsClosed(out string doorReason)) { SendReason(now, doorReason); return; }
            if (AnyCharacterInHull(hull)) { SendReason(now, OrderText.Get("pp.order.dock.character_present", "Стыковка невозможна в отсеке обнаружен человек.")); return; }
            if (hasDelivery) {
                jobCrate = crate; jobClaim = claim; jobSet = deliverySet; jobIncoming = null;
                jobClaim.Reserved = true;
            }
            else {
                jobIncoming = OrderRegistry.TakeNextApproved();
                if (jobIncoming == null) { return; }
                jobSet = incomingSet; jobCrate = null; jobClaim = null;
            }
            SendCue(CueApproach);
            phase = Phase.Approaching;
            phaseUntil = now + ApproachDelay;
        }
        private void CompleteSequence(double now) {
            if (!ZoneStillReady(out Hull hull)) {
                phase = Phase.Idle;
                cooldownUntil = now + JobCooldown;
                SendCue(CueAbort);
                SendReason(now, OrderText.Get("pp.order.dock.aborted_unsealed", "Стыковка прервана отсек больше не изолирован."));
                if (jobIncoming != null) { RequeueIncoming(jobIncoming); }
                ClearJob();
                return;
            }
            SendCue(CueClamp);
            if (jobIncoming != null && jobSet != null) {
                ExecuteIncoming(jobIncoming, jobSet);
            }
            else if (jobCrate is { Removed: false } && jobClaim != null && jobSet != null) {
                ExecuteDelivery(now, jobCrate, jobClaim, jobSet);
            }
            ClearJob();
            phase = Phase.Departing;
            phaseUntil = now + DepartDelay;
        }
        private static void RequeueIncoming(OrderRegistry.PendingOrder order) {
            int id = OrderRegistry.AddPendingIncoming(order.OrderSetId, order.RequestedBy);
            OrderRegistry.TryApprove(id, order.ApprovedReward);
        }
        private void ClearJob() {
            if (jobClaim != null) { jobClaim.Reserved = false; }
            jobCrate = null; jobClaim = null; jobSet = null; jobIncoming = null;
        }
        private bool ZoneStillReady(out Hull hull) {
            hull = item.CurrentHull;
            if (hull == null) { return false; }
            if (OrderRegistry.IsZoneLocked(item.ID)) { return false; }
            if (!AllLinkedDoorsClosed(out _)) { return false; }
            if (AnyCharacterInHull(hull)) { return false; }
            return true;
        }
        private bool AllLinkedDoorsClosed(out string reason) {
            reason = null;
            bool anyDoor = false;
            foreach (MapEntity linked in item.linkedTo) {
                if (linked is Item linkedItem && linkedItem.GetComponent<Door>() is {} door) {
                    anyDoor = true;
                    if (door.IsOpen) { reason = OrderText.Get("pp.order.dock.door_open", "Стыковка невозможна дверь отсека ещё открыта."); return false; }
                }
            }
            if (!anyDoor) { reason = OrderText.Get("pp.order.dock.no_door_linked", "Стыковочная зона не привязана ни к одной двери свяжите её с дверью отсека."); return false; }
            return true;
        }
        private static bool AnyCharacterInHull(Hull hull) {
            foreach (Character c in Character.CharacterList) {
                if (c != null && !c.Removed && !c.IsDead && c.IsHuman && c.CurrentHull == hull) { return true; }
            }
            return false;
        }
        private static int CountMarkedCratesInHull(Hull hull) {
            int n = 0;
            foreach (Item candidate in Item.ItemList) {
                if (candidate == null || candidate.Removed || candidate.CurrentHull != hull) { continue; }
                OrderClaimComponent oc = candidate.GetComponent<OrderClaimComponent>();
                if (oc != null && oc.IsMarked) { n++; }
            }
            return n;
        }
        private bool TryFindDeliveryJob(Hull hull, out Item crate, out OrderRegistry.DeliveryClaim claim,
            out OrderSet set, out string reason) {
            crate = null; claim = null; set = null; reason = null;
            foreach (Item candidate in Item.ItemList) {
                if (candidate == null || candidate.Removed || candidate.CurrentHull != hull) { continue; }
                OrderClaimComponent oc = candidate.GetComponent<OrderClaimComponent>();
                if (oc == null || !oc.IsMarked) { continue; }
                if (!oc.IsDelivery) { continue; }
                if (!OrderRegistry.TryPeekDeliveryClaim(oc.ClaimId, out OrderRegistry.DeliveryClaim candClaim)
                    || candClaim.OrderSetId != (oc.OrderSetId ?? "").ToIdentifier()) {
                    reason = OrderText.Format("pp.order.claim.error.not_found",
                        $"Наряд №{oc.OrderNumber} не найден или уже выполнен.", ("[num]", oc.OrderNumber.ToString()));
                    continue;
                }
                if (!OrderSets.TryGet(candClaim.OrderSetId, out OrderSet candSet)) {
                    reason = OrderText.Format("pp.order.dock.set_missing2",
                        $"Комплект наряда №{oc.OrderNumber} больше не существует.", ("[num]", oc.OrderNumber.ToString()));
                    continue;
                }
                if (!MatchesOrder(candidate, candSet, out string mismatch)) {
                    reason = OrderText.Format("pp.order.dock.claim_mismatch",
                        $"Наряд №{oc.OrderNumber} содержимое не совпадает с заказом ({mismatch}).",
                        ("[num]", oc.OrderNumber.ToString()), ("[mismatch]", mismatch));
                    continue;
                }
                crate = candidate; claim = candClaim; set = candSet;
                return true;
            }
            return false;
        }
        private static bool MatchesOrder(Item crate, OrderSet set, out string mismatch) {
            mismatch = null;
            if (crate.OwnInventory == null) { mismatch = OrderText.Get("pp.order.dock.crate_no_inventory", "у ящика нет отсека"); return false; }
            var counts = new Dictionary<Identifier, int>();
            foreach (Item contained in crate.OwnInventory.AllItems) {
                if (contained == null) { continue; }
                Identifier id = contained.Prefab.Identifier;
                counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
            }
            foreach (OrderItem oi in set.Items) {
                counts.TryGetValue(oi.ItemId, out int have);
                if (have < oi.Count) {
                    string itemName = ItemPrefab.Prefabs.TryGet(oi.ItemId, out ItemPrefab p) ? p.Name.Value : oi.ItemId.Value;
                    mismatch = OrderText.Format("pp.order.dock.mismatch_need",
                        $"нужно {oi.Count}x {itemName}, в ящике {have}",
                        ("[count]", oi.Count.ToString()), ("[item]", itemName), ("[have]", have.ToString()));
                    return false;
                }
            }
            return true;
        }
        private void ExecuteDelivery(double now, Item crate, OrderRegistry.DeliveryClaim claim, OrderSet set) {
            if (!OrderRegistry.TryConsumeDeliveryClaim(claim.Id)) {
                SendReason(now, OrderText.Format("pp.order.dock.already_fulfilled",
                    $"Наряд №{claim.OrderNumber} уже выполнен.", ("[num]", claim.OrderNumber.ToString())));
                return;
            }
            StationEconomyState.ApplyTransaction(null, set.Reward, OrderText.Format("pp.order.dock.comment.delivered",
                $"Доставка {set.DisplayName} (наряд №{claim.OrderNumber})",
                ("[name]", set.DisplayName), ("[num]", claim.OrderNumber.ToString())));
            EconomyNetwork.BroadcastAll();
            if (Entity.Spawner != null) {
                var requiredCounts = new Dictionary<Identifier, int>();
                foreach (OrderItem oi in set.Items) {
                    requiredCounts[oi.ItemId] = oi.Count;
                }
                var items = new List<Item>(crate.OwnInventory.AllItems);
                foreach (Item contained in items) {
                    if (contained == null) { continue; }
                    Identifier id = contained.Prefab.Identifier;
                    if (requiredCounts.TryGetValue(id, out int remaining) && remaining > 0) {
                        Entity.Spawner.AddEntityToRemoveQueue(contained);
                        requiredCounts[id] = remaining - 1;
                    }
                    else {
                        contained.Drop(dropper: null);
                    }
                }
                Entity.Spawner.AddEntityToRemoveQueue(crate);
            }
            SendNearby(OrderText.Format("pp.order.dock.claim_completed",
                $"Доставка принята {set.DisplayName}, наряд №{claim.OrderNumber}. Станции зачислено {set.Reward:N0} кр.",
                ("[name]", set.DisplayName), ("[num]", claim.OrderNumber.ToString()), ("[amount]", set.Reward.ToString("N0"))),
                HintToastNet.SeveritySuccess);
            OrderAnnounce.ShuttleDeparted(claim.Callsign, claim.OrderNumber);
        }
        private void ExecuteIncoming(OrderRegistry.PendingOrder order, OrderSet set) {
            if (Entity.Spawner == null || !ItemPrefab.Prefabs.TryGet(OrderCratePrefabId, out ItemPrefab cratePrefab)) {
                RefundIncoming(order, set);
                return;
            }
            Vector2 spawnPos = item.WorldPosition;
            OrderSet capturedSet = set;
            OrderRegistry.PendingOrder capturedOrder = order;
            Entity.Spawner.AddItemToSpawnQueue(cratePrefab, spawnPos, onSpawned: crate => {
                if (crate == null) { RefundIncoming(capturedOrder, capturedSet); return; }
                if (crate.GetComponent<OrderClaimComponent>() is {} oc) {
                    oc.OrderSetId = capturedSet.Id.Value;
                    oc.ClaimId = capturedOrder.Id;
                    oc.OrderNumber = capturedOrder.Id;
                    oc.ClaimDirection = OrderClaimComponent.DirIncoming;
                    oc.PushSync();
                }
                if (crate.OwnInventory == null) { return; }
                foreach (OrderItem oi in capturedSet.Items) {
                    if (!ItemPrefab.Prefabs.TryGet(oi.ItemId, out ItemPrefab itemPrefab)) { continue; }
                    for (int i = 0; i < oi.Count; i++) {
                        Entity.Spawner.AddItemToSpawnQueue(itemPrefab, crate.OwnInventory);
                    }
                }
            });
            SendNearby(OrderText.Format("pp.order.dock.incoming_arrived",
                $"Закупка №{order.Id} ({set.DisplayName}) прибыла в стыковочную зону.",
                ("[num]", order.Id.ToString()), ("[name]", set.DisplayName)), HintToastNet.SeveritySuccess);
        }
        private void RefundIncoming(OrderRegistry.PendingOrder order, OrderSet set) {
            int refund = order.ApprovedReward > 0 ? order.ApprovedReward : (set?.BuyPrice ?? 0);
            if (refund > 0) {
                StationEconomyState.ApplyTransaction(null, refund, OrderText.Format("pp.order.dock.comment.refund_undeliverable",
                    $"Возврат за закупку №{order.Id} доставка невозможна", ("[num]", order.Id.ToString())));
                EconomyNetwork.BroadcastAll();
            }
            HintSender.SendAll(OrderText.Format("pp.order.dock.buy_failed",
                $"Закупка №{order.Id} груз доставить не удалось, {refund:N0} кр. возвращены на баланс станции.",
                ("[num]", order.Id.ToString()), ("[amount]", refund.ToString("N0"))), HintToastNet.SeverityError);
        }
        private void SendReason(double now, string text) {
            if (text == lastReason && now < lastReasonTime + ReasonRepeatSeconds) { return; }
            lastReason = text;
            lastReasonTime = now;
            HintSender.SendNear(item.WorldPosition, ReasonRange, text, HintToastNet.SeverityError);
        }
        private void SendNearby(string text, byte severity)
            => HintSender.SendNear(item.WorldPosition, ReasonRange, text, severity);
        private void SendCue(byte cue) => item.CreateServerEvent(this, new SoundCueEventData(cue));
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            byte cue = TryExtractEventData(extraData, out SoundCueEventData data) ? data.Cue : CueApproach;
            msg.WriteByte(cue);
        }
    }
}
