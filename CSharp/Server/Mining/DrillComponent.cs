using Barotrauma;
using Barotrauma.Networking;

namespace PrimaryPackage.Mining {
    public partial class DrillComponent {
        partial void OnStateChanged() {
            RequestSync();
        }

        partial void RequestSync() {
            if (GameMain.Server != null && !Submarine.Unloading) {
                item.CreateServerEvent(this);
            }
        }

        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            msg.WriteBoolean(State);
            msg.WriteSingle(Intensity);
            msg.WriteSingle(Heat);
            msg.WriteBoolean(Overheated);
        }
    }
}
