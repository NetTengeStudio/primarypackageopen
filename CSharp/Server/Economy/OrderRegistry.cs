using System;
using System.Collections.Generic;
using Barotrauma;
namespace PrimaryPackage.Economy {
    internal static class OrderRegistry {
        public sealed class PendingOrder {
            public int Id;
            public Identifier OrderSetId;
            public string RequestedBy;
            public bool Approved;
            public int ApprovedReward;
            public double CreatedTime;
        }
        public sealed class DeliveryClaim {
            public int Id;
            public Identifier OrderSetId;
            public string RequestedBy;
            public OrderState State = OrderState.Accepted;
            public double DeadlineTime;
            public string Callsign;
            public bool ApproachAnnounced;
            public int OrderNumber;
            public bool Reserved;
            public OrderState FailedFrom;
        }
        private static readonly string[] CallsignPrefixes = { "А", "Б", "Т", "К", "М" };
        private static readonly Random rng = new Random();
        private static string GenerateCallsign() {
            string prefix = CallsignPrefixes[rng.Next(CallsignPrefixes.Length)];
            return $"{prefix} {rng.Next(1, 10000):0000}";
        }
        private static int GenerateOrderNumber() {
            for (int attempt = 0; attempt < 20; attempt++) {
                int n = rng.Next(100_000_000, 1_000_000_000);
                bool used = false;
                foreach (DeliveryClaim c in claims) { if (c.OrderNumber == n) { used = true; break; } }
                if (!used) { return n; }
            }
            return rng.Next(100_000_000, 1_000_000_000);
        }
        public const double OrderDurationSeconds = 900.0;
        public static bool ManualApprovalRequired = false;
        private static int nextId = 1;
        private static readonly List<PendingOrder> pending = new();
        private static readonly List<DeliveryClaim> claims = new();
        private static readonly HashSet<int> lockedZones = new();
        public static IReadOnlyList<PendingOrder> Pending => pending;
        public static IReadOnlyList<DeliveryClaim> Claims => claims;
        public const int MaxOutstandingClaimsPerRequester = 5;
        public const double PendingApprovalTimeoutSeconds = 1800.0;
        public static int OutstandingClaimCount(string requestedBy) {
            int count = 0;
            foreach (DeliveryClaim c in claims) { if (c.RequestedBy == requestedBy) { count++; } }
            return count;
        }
        public static int AddPendingIncoming(Identifier orderSetId, string requestedBy) {
            var order = new PendingOrder { Id = nextId++, OrderSetId = orderSetId, RequestedBy = requestedBy, Approved = false, CreatedTime = Timing.TotalTime };
            pending.Add(order);
            return order.Id;
        }
        public static DeliveryClaim AddDeliveryClaim(Identifier orderSetId, string requestedBy) {
            var claim = new DeliveryClaim {
                Id = nextId++,
                OrderSetId = orderSetId,
                RequestedBy = requestedBy,
                State = OrderState.Accepted,
                DeadlineTime = Timing.TotalTime + OrderDurationSeconds,
                Callsign = GenerateCallsign(),
                OrderNumber = GenerateOrderNumber(),
            };
            claims.Add(claim);
            return claim;
        }
        private static double nextExpiryTick;
        public static bool ShouldTickExpiry(double now) {
            if (now < nextExpiryTick) { return false; }
            nextExpiryTick = now + 1.0;
            return true;
        }
        public static List<DeliveryClaim> GetClaimsEnteringApproach(double now, double leadTime) {
            var list = new List<DeliveryClaim>();
            foreach (DeliveryClaim c in claims) {
                if (c.State != OrderState.InProgress || c.ApproachAnnounced) { continue; }
                double remaining = c.DeadlineTime - now;
                if (remaining > 0 && remaining <= leadTime) {
                    c.ApproachAnnounced = true;
                    c.State = OrderState.AwaitingShuttle;
                    list.Add(c);
                }
            }
            return list;
        }
        public static List<DeliveryClaim> TakeExpiredClaims(double now) {
            var expired = new List<DeliveryClaim>();
            for (int i = claims.Count - 1; i >= 0; i--) {
                DeliveryClaim c = claims[i];
                if (c.State == OrderState.Collected || c.State == OrderState.Failed || c.Reserved) { continue; }
                if (now >= c.DeadlineTime) {
                    c.FailedFrom = c.State;
                    c.State = OrderState.Failed;
                    expired.Add(c);
                    claims.RemoveAt(i);
                }
            }
            return expired;
        }
        public const double FailPenaltyFraction = 0.5;
        public static int ApplyFailurePenalty(DeliveryClaim claim) {
            OrderClaimComponent.UnmarkByClaimId(claim.Id);
            if (claim.FailedFrom == OrderState.Accepted) { return 0; }
            bool haveSet = OrderSets.TryGet(claim.OrderSetId, out OrderSet set);
            string name = haveSet ? set.DisplayName : claim.OrderSetId.Value;
            int reward = haveSet ? set.Reward : 0;
            int penalty = (int)Math.Round(Math.Max(0, reward) * FailPenaltyFraction);
            if (penalty > 0) {
                StationEconomyState.ApplyTransaction(null, -penalty, OrderText.Format("pp.order.penalty.comment",
                    $"Провал наряда №{claim.OrderNumber} «{name}» штраф",
                    ("[num]", claim.OrderNumber.ToString()),
                    ("[name]", name)));
                EconomyNetwork.BroadcastAll();
            }
            OrderAnnounce.ShuttleFailed(claim.Callsign, claim.OrderNumber);
            return penalty;
        }
        public static List<PendingOrder> TakeExpiredPending(double now) {
            var expired = new List<PendingOrder>();
            for (int i = pending.Count - 1; i >= 0; i--) {
                PendingOrder o = pending[i];
                if (o.Approved || now - o.CreatedTime < PendingApprovalTimeoutSeconds) { continue; }
                expired.Add(o);
                pending.RemoveAt(i);
            }
            return expired;
        }
        private static double nextGlobalTick;
        public static void TickExpiryGlobal(double now) {
            if (now < nextGlobalTick) { return; }
            nextGlobalTick = now + 1.0;
            foreach (DeliveryClaim expired in TakeExpiredClaims(now)) {
                int penalty = ApplyFailurePenalty(expired);
                HintSender.SendAll(
                    penalty > 0
                        ? OrderText.Format("pp.order.expired.penalty",
                            $"Наряд №{expired.OrderNumber} провален груз не доставлен вовремя. Штраф {penalty:N0} кр.",
                            ("[num]", expired.OrderNumber.ToString()),
                            ("[penalty]", penalty.ToString("N0")))
                        : OrderText.Format("pp.order.expired.nopenalty",
                            $"Наряд №{expired.OrderNumber} провален груз не доставлен вовремя.",
                            ("[num]", expired.OrderNumber.ToString())),
                    HintToastNet.SeverityError);
            }
            foreach (PendingOrder timedOut in TakeExpiredPending(now)) {
                HintSender.SendAll(
                    OrderText.Format("pp.order.pending.timeout",
                        $"Заявка на закупку №{timedOut.Id} отклонена автоматически истекло время ожидания одобрения ГМ.",
                        ("[num]", timedOut.Id.ToString())),
                    HintToastNet.SeverityError);
            }
        }
        public static bool TryPeekDeliveryClaim(int id, out DeliveryClaim claim) {
            foreach (DeliveryClaim c in claims) {
                if (c.Id == id) { claim = c; return true; }
            }
            claim = null;
            return false;
        }
        public static bool TryConsumeDeliveryClaim(int id) {
            for (int i = 0; i < claims.Count; i++) {
                if (claims[i].Id == id) { claims.RemoveAt(i); return true; }
            }
            return false;
        }
        public static bool TryApprove(int id, int reward) {
            foreach (PendingOrder o in pending) {
                if (o.Id == id) { o.Approved = true; o.ApprovedReward = reward; return true; }
            }
            return false;
        }
        public static bool TryGetPending(int id, out PendingOrder order) {
            foreach (PendingOrder o in pending) {
                if (o.Id == id) { order = o; return true; }
            }
            order = null;
            return false;
        }
        public static bool TryReject(int id, out PendingOrder removed) {
            for (int i = 0; i < pending.Count; i++) {
                if (pending[i].Id == id) { removed = pending[i]; pending.RemoveAt(i); return true; }
            }
            removed = null;
            return false;
        }
        public static PendingOrder PeekNextApproved() {
            foreach (PendingOrder o in pending) {
                if (o.Approved) { return o; }
            }
            return null;
        }
        public static PendingOrder TakeNextApproved() {
            for (int i = 0; i < pending.Count; i++) {
                if (pending[i].Approved) { PendingOrder o = pending[i]; pending.RemoveAt(i); return o; }
            }
            return null;
        }
        public static void LockZone(int zoneItemId) => lockedZones.Add(zoneItemId);
        public static void UnlockZone(int zoneItemId) => lockedZones.Remove(zoneItemId);
        public static bool IsZoneLocked(int zoneItemId) => lockedZones.Contains(zoneItemId);
        public static void Clear() {
            OrderClaimComponent.UnmarkAll();
            pending.Clear();
            claims.Clear();
            lockedZones.Clear();
            nextId = 1;
        }
    }
}
