using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class StationBalanceComponent {
        public override void OnItemLoaded() {
            base.OnItemLoaded();
            if (GameMain.Server != null) EconomyNetwork.Register(item);
        }
        public void ServerEventRead(IReadMessage msg, Client c) {
            if (!item.CanClientAccess(c)) return;
            PushFullSync();
        }
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
            => EconomyNetIO.WriteState(msg);
        public void PushFullSync() => item.CreateServerEvent(this, new FullSyncEventData());
    }
}
