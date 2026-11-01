using System;
using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    public partial class OrderCheckerComponent {
        public void ServerEventRead(IReadMessage msg, Client c) {
            if (!item.CanClientAccess(c)) { return; }
            PushSnapshot();
        }
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            double now = Timing.TotalTime;
            var claims = OrderRegistry.Claims;
            int count = Math.Min(claims.Count, 255);
            msg.WriteByte((byte)count);
            for (int i = 0; i < count; i++) {
                OrderRegistry.DeliveryClaim claim = claims[i];
                string name = OrderSets.TryGet(claim.OrderSetId, out OrderSet set) ? set.DisplayName : claim.OrderSetId.Value;
                float remaining = (float)Math.Max(0.0, claim.DeadlineTime - now);
                msg.WriteInt32(claim.OrderNumber);
                msg.WriteString(name);
                msg.WriteByte((byte)claim.State);
                msg.WriteSingle(remaining);
            }
        }
        public void PushSnapshot() => item.CreateServerEvent(this, new SnapshotEventData());
    }
}
