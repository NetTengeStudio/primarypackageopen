using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Documents {
    public partial class DocumentMarkComponent {
        public override void OnItemLoaded() {
            base.OnItemLoaded();
            PrimaryPackage.CoalitionDocument.CoalitionDocumentRenderer.UpdateDocument(item, forceBake: true);
        }
        public override void Update(float deltaTime, Camera cam) {
            base.Update(deltaTime, cam);
            PrimaryPackage.CoalitionDocument.CoalitionDocumentRenderer.UpdateDocument(item, forceBake: false);
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime) {
            StampsData = msg.ReadString();
            PrimaryPackage.CoalitionDocument.CoalitionDocumentRenderer.UpdateDocument(item, forceBake: true);
        }
    }
}
