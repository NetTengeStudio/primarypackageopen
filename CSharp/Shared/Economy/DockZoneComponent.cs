using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class DockZoneComponent : ItemComponent, IServerSerializable {
        public const byte CueApproach = 0;
        public const byte CueClamp = 1;
        public const byte CueUndock = 2;
        public const byte CueAbort = 3;
        [Editable, Serialize("Стыковочная зона", IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public string ZoneName { get; set; } = "Стыковочная зона";
        public readonly record struct SoundCueEventData(byte Cue) : ItemComponent.IEventData;
        public DockZoneComponent(Item item, ContentXElement element) : base(item, element) {
            IsActive = true;
            InitProjSpecific();
        }
        public override bool ValidateEventData(NetEntityEvent.IData data)
            => TryExtractEventData<SoundCueEventData>(data, out _);
        public void ClientEventRead(IReadMessage msg, float sendingTime) => ClientEventReadProjSpecific(msg, sendingTime);
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime);
        partial void InitProjSpecific();
    }
}
