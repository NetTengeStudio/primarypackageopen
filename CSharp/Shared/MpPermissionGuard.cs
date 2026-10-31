using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace PrimaryPackage {
    public static class MpPermissionGuard {
        public static bool CheckOrWarn(Client sender, ClientPermissions permission, string feature) {
            if (sender != null && sender.HasPermission(permission)) { return true; }
            DebugConsole.AddWarning(feature + ": отклонено — у " + (sender?.Name ?? "?") + " нет права " + permission + ".");
            return false;
        }

#if CLIENT
        public static bool WarnIfMissing(GameClient client, ClientPermissions permission, string feature) {
            if (client == null || client.HasPermission(permission)) { return true; }
            DebugConsole.NewMessage(
                feature + ": у вас нет права " + permission + " — правки будут видны только локально, " +
                "сервер их не примет и они не повлияют на реальный бой/холод.", Color.Orange);
            return false;
        }
#endif
    }
}
