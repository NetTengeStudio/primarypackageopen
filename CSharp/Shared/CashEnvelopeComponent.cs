using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public class CashEnvelopeComponent : ItemComponent, IServerSerializable {
        public const string EnvelopeTag = "primarypkg_cashenvelope";
        public static bool IsEnvelopeItem(Item it) => it != null && it.HasTag(EnvelopeTag);
        [Serialize(0, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public int Amount { get; set; }
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string IssuedTo { get; set; } = "";
        public CashEnvelopeComponent(Item item, ContentXElement element) : base(item, element) {}
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            msg.WriteInt32(Amount);
            msg.WriteString(IssuedTo ?? "");
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime) {
            Amount = msg.ReadInt32();
            IssuedTo = msg.ReadString();
            UpdateDescription();
        }
        public void UpdateDescription() {
            Item.Description = OrderText.Format("pp.order.envelope.body",
                $"КОНВЕРТ С НАЛИЧНЫМИ {Amount:N0} кр.\nПолучатель {IssuedTo}",
                ("[amount]", Amount.ToString("N0")),
                ("[who]", IssuedTo ?? ""));
        }
    }
}
