using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public class CheckComponent : ItemComponent, IServerSerializable {
        public const string KindClaim = "claim";
        public const string KindReceipt = "receipt";
        public const string CheckTag = "primarypkg_check";
        public const string OrderCrateTag = "primarypkg_ordercrate";
        public const string PrintNetId = "primarypkg_check_print";
        private static readonly Identifier CheckTagId = CheckTag;
        private static readonly Identifier OrderCrateTagId = OrderCrateTag;
        public static bool IsCheckItem(Item it) => it != null && it.HasTag(CheckTagId);
        public static bool IsOrderCrateItem(Item it) => it != null && it.HasTag(OrderCrateTagId);
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string CheckKind { get; set; } = "";
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string OrderSetId { get; set; } = "";
        [Serialize(0, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public int ClaimId { get; set; }
        [Serialize(0, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public int OrderNumber { get; set; }
        [Serialize(0, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public int Amount { get; set; }
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string IssuedTo { get; set; } = "";
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string IssuedDate { get; set; } = "";
        public bool IsClaim => CheckKind == KindClaim;
        public bool IsReceipt => CheckKind == KindReceipt;
        public CheckComponent(Item item, ContentXElement element) : base(item, element) {}
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            msg.WriteString(CheckKind ?? "");
            msg.WriteString(OrderSetId ?? "");
            msg.WriteInt32(ClaimId);
            msg.WriteInt32(OrderNumber);
            msg.WriteInt32(Amount);
            msg.WriteString(IssuedTo ?? "");
            msg.WriteString(IssuedDate ?? "");
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime) {
            CheckKind = msg.ReadString();
            OrderSetId = msg.ReadString();
            ClaimId = msg.ReadInt32();
            OrderNumber = msg.ReadInt32();
            Amount = msg.ReadInt32();
            IssuedTo = msg.ReadString();
            IssuedDate = msg.ReadString();
            UpdateDescription();
        }
        public void UpdateDescription() {
            if (IsClaim) {
                bool haveSet = OrderSets.TryGet((OrderSetId ?? "").ToIdentifier(), out OrderSet set);
                string name = haveSet ? set.DisplayName : (OrderSetId ?? "?");
                string contents = haveSet ? CheckMint.FormatOrderContents(set) : "нет";
                string reward = haveSet ? set.Reward.ToString("N0") : "0";
                Item.Description = OrderText.Format("pp.order.check.claim",
                    $"НАРЯД №{OrderNumber} {name}.\nДоставить {contents}.\nНаграда станции {reward} кр.\n" +
                    "Привяжите чек к ящику наряда возьмите чек в руки и нажмите «Взаимодействовать» по ящику или просто положите чек внутрь ящика.",
                    ("[num]", OrderNumber.ToString()),
                    ("[name]", name),
                    ("[contents]", contents),
                    ("[reward]", reward));
            }
            else if (IsReceipt) {
                Item.Description = OrderText.Format("pp.order.check.receipt",
                    $"ЧЕК НА ВЫДАЧУ {Amount:N0} КР.\nПолучатель {IssuedTo}\nДата {IssuedDate}",
                    ("[amount]", Amount.ToString("N0")),
                    ("[who]", IssuedTo ?? ""),
                    ("[date]", IssuedDate ?? ""));
            }
        }
    }
    public static class CheckMint {
        private static readonly Identifier CheckPrefabId = "pp_check".ToIdentifier();
        private static readonly Identifier CashEnvelopePrefabId = "pp_cashenvelope".ToIdentifier();
        public static bool SpawnClaimCheck(Character receiver, OrderSet set, int claimId, int orderNumber, System.Action onFailure = null) {
            return Spawn(receiver, check => {
                check.CheckKind = CheckComponent.KindClaim;
                check.OrderSetId = set.Id.Value;
                check.ClaimId = claimId;
                check.OrderNumber = orderNumber;
                check.UpdateDescription();
            }, onFailure);
        }
        public static bool SpawnReceiptCheck(Character receiver, int amount, string who, string date, System.Action onFailure = null) {
            return Spawn(receiver, check => {
                check.CheckKind = CheckComponent.KindReceipt;
                check.Amount = amount;
                check.IssuedTo = who ?? "";
                check.IssuedDate = date ?? "";
                check.UpdateDescription();
            }, onFailure);
        }
        public static bool SpawnCashEnvelope(Character receiver, int amount, string who, System.Action onFailure = null) {
            if (receiver?.Inventory == null || Entity.Spawner == null) { return false; }
            if (!ItemPrefab.Prefabs.TryGet(CashEnvelopePrefabId, out ItemPrefab prefab)) { return false; }
            Entity.Spawner.AddItemToSpawnQueue(prefab, receiver.Inventory, onSpawned: it => {
                CashEnvelopeComponent env = it?.GetComponent<CashEnvelopeComponent>();
                if (env == null) { onFailure?.Invoke(); return; }
                env.Amount = amount;
                env.IssuedTo = who ?? "";
                env.UpdateDescription();
#if SERVER
                it.CreateServerEvent(env);
#endif
            });
            return true;
        }
        public static string FormatOrderContents(OrderSet set) {
            var sb = new System.Text.StringBuilder();
            foreach (OrderItem oi in set.Items) {
                if (sb.Length > 0) { sb.Append(", "); }
                string itemName = ItemPrefab.Prefabs.TryGet(oi.ItemId, out ItemPrefab p) ? p.Name.Value : oi.ItemId.Value;
                sb.Append(oi.Count).Append("x ").Append(itemName);
            }
            return sb.Length > 0 ? sb.ToString() : "нет";
        }
        private static bool Spawn(Character receiver, System.Action<CheckComponent> configure, System.Action onFailure = null) {
            if (receiver?.Inventory == null || Entity.Spawner == null) { return false; }
            if (!ItemPrefab.Prefabs.TryGet(CheckPrefabId, out ItemPrefab prefab)) { return false; }
            Entity.Spawner.AddItemToSpawnQueue(prefab, receiver.Inventory, onSpawned: it => {
                CheckComponent check = it?.GetComponent<CheckComponent>();
                if (check == null) { onFailure?.Invoke(); return; }
                configure(check);
#if SERVER
                it.CreateServerEvent(check);
#endif
            });
            return true;
        }
    }
}
