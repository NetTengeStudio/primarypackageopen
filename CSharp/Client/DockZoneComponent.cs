using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class DockZoneComponent {
        partial void InitProjSpecific() {}
        partial void ClientEventReadProjSpecific(IReadMessage msg, float sendingTime) {
            byte cue = msg.ReadByte();
            PlaySound(cue switch {
                CueClamp => ActionType.OnSecondaryUse,
                CueUndock => ActionType.OnBroken,
                CueAbort => ActionType.OnFailure,
                _ => ActionType.OnUse
            });
        }
    }
}
