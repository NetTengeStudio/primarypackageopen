using Barotrauma.Networking;
namespace PrimaryPackage.Documents {
    public partial class DocumentMarkComponent {
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) {
            msg.WriteString(StampsData ?? "");
        }
    }
}
