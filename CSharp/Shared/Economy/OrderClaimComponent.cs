using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class OrderClaimComponent : ItemComponent, IServerSerializable {
        public const string DirDelivery = "delivery";
        public const string DirIncoming = "incoming";
        [Serialize("", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string OrderSetId { get; set; } = "";
        [Serialize(0, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public int ClaimId { get; set; }
        [Serialize(0, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public int OrderNumber { get; set; }
        [Serialize(DirDelivery, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string ClaimDirection { get; set; } = DirDelivery;
        public bool IsMarked => ClaimId > 0;
        public bool IsDelivery => ClaimDirection != DirIncoming;
        public readonly struct SyncEventData : ItemComponent.IEventData {}
        public OrderClaimComponent(Item item, ContentXElement element) : base(item, element) {
            InitProjSpecific();
            IsActive = true;
        }
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<SyncEventData>(data, out _);
        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void InitProjSpecific();
    }
}
