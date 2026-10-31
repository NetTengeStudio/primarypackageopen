using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.CrewManifest {
    public partial class CrewManifestComponent : ItemComponent, IClientSerializable, IServerSerializable {
        public readonly struct RequestEventData : ItemComponent.IEventData {
            public readonly Identifier JobId;
            public RequestEventData(Identifier jobId) { JobId = jobId; }
        }
        public readonly struct FullSyncRequestEventData : ItemComponent.IEventData {}
        public readonly struct BroadcastEventData : ItemComponent.IEventData {
            public readonly ushort TargetId;
            public BroadcastEventData(ushort targetId) { TargetId = targetId; }
        }
        public readonly struct FullSyncEventData : ItemComponent.IEventData {}
        public CrewManifestComponent(Item item, ContentXElement element) : base(item, element) {
            CanBeSelected = true;
            InitProjSpecific();
        }
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<RequestEventData>(data, out _)
            || TryExtractEventData<FullSyncRequestEventData>(data, out _)
            || TryExtractEventData<BroadcastEventData>(data, out _)
            || TryExtractEventData<FullSyncEventData>(data, out _);
        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => ClientEventWriteProjSpecific(msg, extraData);
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void ClientEventWriteProjSpecific(IWriteMessage msg, NetEntityEvent.IData extraData);
        partial void InitProjSpecific();
        private static void WriteId(IWriteMessage msg, Identifier id) => msg.WriteString(id.IsEmpty ? "" : id.Value);
        private static Identifier ReadId(IReadMessage msg) => msg.ReadString().ToIdentifier();
    }
}
