using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class OrderClaimComponent {
        public override void OnItemLoaded() {
            base.OnItemLoaded();
            UpdateBake();
        }
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            OrderSetId = msg.ReadString();
            ClaimId = msg.ReadInt32();
            ClaimDirection = msg.ReadString();
            OrderNumber = msg.ReadInt32();
            UpdateBake();
        }
        private void UpdateBake() {
            if (IsMarked) {
                bool haveSet = OrderSets.TryGet((OrderSetId ?? "").ToIdentifier(), out OrderSet set);
                string name = haveSet ? set.DisplayName : (OrderSetId ?? "?");
                string num = OrderNumber > 0 ? OrderNumber.ToString() : ClaimId.ToString();
                OrderCrateBaker.Request(item, $"№{num}", name);
                string contents = haveSet ? CheckMint.FormatOrderContents(set) : "нет";
                item.Description = IsDelivery
                    ? OrderText.Format("pp.order.crate.marked",
                        $"Наряд №{num} привязан к ящику {name}.\nВложите точный состав {contents}.\nЗатем предъявите ящик в стыковочной зоне.",
                        ("[num]", num),
                        ("[name]", name),
                        ("[contents]", contents))
                    : OrderText.Format("pp.order.crate.incoming",
                        $"Закупка №{num} доставлена {name}.\nСодержимое {contents}.\nЯщик можно освободить и использовать для новых нарядов.",
                        ("[num]", num),
                        ("[name]", name),
                        ("[contents]", contents));
            }
            else {
                OrderCrateBaker.Release(item);
                if (item.Prefab?.Description != null) { item.Description = item.Prefab.Description.Value; }
            }
        }
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            OrderCrateBaker.ProcessPending();
        }
        public override void RemoveComponentSpecific() {
            base.RemoveComponentSpecific();
            OrderCrateBaker.Release(item);
        }
        public override void ShallowRemoveComponentSpecific() {
            base.ShallowRemoveComponentSpecific();
            OrderCrateBaker.Release(item);
        }
    }
}
