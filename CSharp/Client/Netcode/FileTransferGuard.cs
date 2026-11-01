using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.Netcode {
    public sealed class FileTransferGuardPlugin : PrimaryPackage.Core.IModule {
        private const string LogName = "pp_nettrace.log";
        private const int MaxReports = 5;

        private static AccessTools.FieldRef<FileReceiver, List<FileReceiver.FileTransferIn>> transfersRef;
        private static MethodInfo connectionSetter;
        private static int reports;
        private static int repaired;

        private Harmony harmony;

        public static int Repaired => repaired;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            try {
                transfersRef = AccessTools.FieldRefAccess<FileReceiver, List<FileReceiver.FileTransferIn>>("activeTransfers");
                connectionSetter = AccessTools.PropertySetter(typeof(FileReceiver.FileTransferIn), nameof(FileReceiver.FileTransferIn.Connection));
                harmony.Patch(
                    AccessTools.Method(typeof(FileReceiver), nameof(FileReceiver.ReadMessage)),
                    prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(FileTransferGuardPlugin), nameof(ReadMessagePrefix)),
                    postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(FileTransferGuardPlugin), nameof(ReadMessagePostfix)),
                    finalizer: PrimaryPackage.Core.ModuleHost.Bind(typeof(FileTransferGuardPlugin), nameof(ReadMessageFinalizer)));
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

        public static Exception ReadMessageFinalizer(Exception __exception) {
            if (__exception == null) { return null; }
            if (reports < MaxReports) {
                reports++;
                Log(__exception);
            }
            return null;
        }

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

        private static void Log(Exception exception) {
            DebugConsole.ThrowError("pp_net: сбой разбора сообщения о передаче файла", exception);
            try {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Daedalic Entertainment GmbH", "Barotrauma");
                if (!Directory.Exists(dir)) { return; }
                StringBuilder text = new StringBuilder();
                text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\t')
                    .Append(exception).Append('\n');
                File.AppendAllText(Path.Combine(dir, LogName), text.ToString(), Encoding.UTF8);
            } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("FileTransferGuard.1", noted); }
        }
    }
}
