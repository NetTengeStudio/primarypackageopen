using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class StationBalanceComponent : ItemComponent, IClientSerializable, IServerSerializable {
        public readonly struct FullSyncRequestEventData : ItemComponent.IEventData {}
        public readonly struct FullSyncEventData : ItemComponent.IEventData {}
        public StationBalanceComponent(Item item, ContentXElement element) : base(item, element) {
            CanBeSelected = true;
            InitProjSpecific();
        }
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<FullSyncRequestEventData>(data, out _)
            || TryExtractEventData<FullSyncEventData>(data, out _);
        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => ClientEventWriteProjSpecific(msg, extraData);
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData);
        partial void InitProjSpecific();
    }
}
