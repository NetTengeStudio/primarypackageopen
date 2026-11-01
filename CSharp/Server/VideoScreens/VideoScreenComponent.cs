using Barotrauma;
using Barotrauma.Networking;

namespace PrimaryPackage.VideoScreens {
    public partial class VideoScreen {
        private const double ResyncInterval = 10.0;

        private double lastSyncSentAt = double.NegativeInfinity;

        partial void PeriodicSync() {
            if (!lastEffectiveOn) { return; }
            if (Timing.TotalTime - lastSyncSentAt >= ResyncInterval) { PushSync(); }
        }

        partial void PushSync() {
            lastSyncSentAt = Timing.TotalTime;
            item.CreateServerEvent(this, new SyncEventData());
        }

        public void ServerEventRead(IReadMessage msg, Client c) => PushSync();

        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            msg.WriteBoolean(EffectiveOn);
        }
    }
}
