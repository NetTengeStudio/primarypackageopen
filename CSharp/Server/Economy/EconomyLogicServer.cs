using Barotrauma;
using Barotrauma.Networking;
namespace PrimaryPackage.Economy {
    internal static partial class EconomyLogic {
        static partial void CheckServerAdminPermissionProjSpecific(Character requester, ref bool isAdmin) {
            if (GameMain.Server == null) return;
            var client = GameMain.Server.ConnectedClients.Find(c => c.Character == requester);
            if (client != null && client.HasPermission(ClientPermissions.ManageRound)) isAdmin = true;
        }
    }
}
