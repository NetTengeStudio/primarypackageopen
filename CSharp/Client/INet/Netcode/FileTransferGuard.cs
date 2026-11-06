using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;

namespace INet {
    public sealed class FileTransferGuardPlugin : PrimaryPackage.Core.IModule {
        private static AccessTools.FieldRef<FileReceiver, List<FileReceiver.FileTransferIn>> transfersRef;
        private static MethodInfo connectionSetter;
        private static int repaired;

        public static int Repaired => repaired;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            try {
                transfersRef = AccessTools.FieldRefAccess<FileReceiver, List<FileReceiver.FileTransferIn>>("activeTransfers");
                connectionSetter = AccessTools.PropertySetter(typeof(FileReceiver.FileTransferIn), nameof(FileReceiver.FileTransferIn.Connection));
                harmony.Patch(
                    AccessTools.Method(typeof(FileReceiver), nameof(FileReceiver.ReadMessage)),
                    prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(FileTransferGuardPlugin), nameof(ReadMessagePrefix)),
                    postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(FileTransferGuardPlugin), nameof(ReadMessagePostfix)));
            } catch (Exception e) {
                DebugConsole.ThrowError("pp_net: не удалось установить патч FileReceiver.ReadMessage", e);
            }
        }
        public void Release() {
            transfersRef = null;
            connectionSetter = null;
        }

        public static void ReadMessagePrefix(FileReceiver __instance) => Repair(__instance);

        public static void ReadMessagePostfix(FileReceiver __instance) => Repair(__instance);

        private static void Repair(FileReceiver receiver) {
            if (receiver == null || transfersRef == null || connectionSetter == null) { return; }
            NetworkConnection server = GameMain.Client?.ClientPeer?.ServerConnection;
            if (server == null) { return; }
            List<FileReceiver.FileTransferIn> transfers = transfersRef(receiver);
            if (transfers == null) { return; }
            for (int i = 0; i < transfers.Count; i++) {
                FileReceiver.FileTransferIn transfer = transfers[i];
                if (transfer == null || transfer.Connection != null) { continue; }
                try {
                    connectionSetter.Invoke(transfer, new object[] { server });
                    repaired++;
                    DebugConsole.AddWarning("pp_net: у передачи файла " + transfer.FileName +
                        " не было соединения (фрагментированный пакет), подставлено соединение с сервером.");
                } catch (Exception e) {
                    DebugConsole.ThrowError("pp_net: не удалось починить передачу файла " + transfer.FileName, e);
                    connectionSetter = null;
                    return;
                }
            }
        }
    }
}
