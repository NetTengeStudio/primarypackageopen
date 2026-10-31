using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class StationAtmComponent : ItemComponent, IClientSerializable, IServerSerializable {
        public const int MaxWithdrawComment = 120;
        public readonly record struct WithdrawRequestEventData(int Amount, string Comment) : ItemComponent.IEventData;
        public readonly struct FullSyncRequestEventData : ItemComponent.IEventData {}
        public readonly struct FullSyncEventData : ItemComponent.IEventData {}
        public const byte CashInModeEnvelope = 0;
        public const byte CashInModeStation = 1;
        public const byte CashInModeEnvelopeToWallet = 2;
        public readonly record struct CashInRequestEventData(ushort ItemId, byte Mode) : ItemComponent.IEventData;
        public StationAtmComponent(Item item, ContentXElement element) : base(item, element) {
            CanBeSelected = true;
            InitProjSpecific();
        }
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<WithdrawRequestEventData>(data, out _)
            || TryExtractEventData<CashInRequestEventData>(data, out _)
            || TryExtractEventData<FullSyncRequestEventData>(data, out _)
            || TryExtractEventData<FullSyncEventData>(data, out _);
        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => ClientEventWriteProjSpecific(msg, extraData);
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData);
        partial void InitProjSpecific();
    }
}
