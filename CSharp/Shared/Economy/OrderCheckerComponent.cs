using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class OrderCheckerComponent : ItemComponent, IClientSerializable, IServerSerializable {
        public readonly struct RefreshRequestEventData : ItemComponent.IEventData {}
        public readonly struct SnapshotEventData : ItemComponent.IEventData {}
        public OrderCheckerComponent(Item item, ContentXElement element) : base(item, element) {
            CanBeSelected = true;
            InitProjSpecific();
        }
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<RefreshRequestEventData>(data, out _)
            || TryExtractEventData<SnapshotEventData>(data, out _);
        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => ClientEventWriteProjSpecific(msg, extraData);
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData);
        partial void InitProjSpecific();
    }
}
