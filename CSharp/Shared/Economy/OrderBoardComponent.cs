using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class OrderBoardComponent : ItemComponent, IClientSerializable {
        public const byte DirectionDelivery = 0;
        public const byte DirectionIncoming = 1;
        public readonly record struct OrderRequestEventData(string OrderSetId, byte Direction) : ItemComponent.IEventData;
        public OrderBoardComponent(Item item, ContentXElement element) : base(item, element) {
            CanBeSelected = true;
            InitProjSpecific();
        }
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<OrderRequestEventData>(data, out _);
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => ClientEventWriteProjSpecific(msg, extraData);
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData);
        partial void InitProjSpecific();
    }
}
