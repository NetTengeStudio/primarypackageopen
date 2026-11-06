using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;

namespace INet {
    public sealed class SyncRecoveryPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() => SyncRecovery.Reset();

        public void Attach(Harmony harmony) {
            try {
                harmony.Patch(
                    AccessTools.Method(typeof(ServerEntityEventManager), "Update", new[] { typeof(List<Client>) }),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(SyncRecovery), nameof(SyncRecovery.SampleProgress))));
                harmony.Patch(
                    AccessTools.Method(typeof(GameServer), "DisconnectClient", new[] { typeof(Client), typeof(PeerDisconnectPacket) }),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(SyncRecovery), nameof(SyncRecovery.VetoDesyncDisconnect))));
            }
            catch (Exception e) {
                DebugConsole.ThrowError("syncrecovery не удалось поставить патчи, восстановление синхронизации не активно", e);
            }
        }
        public void Release() => SyncRecovery.Reset();
    }

    internal static class SyncRecovery {

        private sealed class RecoveryState {
            public bool Init;
            public ushort LastRecvId;
            public double LastProgressTime;
            public double RecoveryStartTime;
            public double LastVetoTime;
            public double NextOwnerWarning;
        }

        private static readonly ConditionalWeakTable<Client, RecoveryState> States = new();

        internal static long Vetoes;

        internal static void Reset() { States.Clear(); }

        internal static void SampleProgress(List<Client> clients) {
            if (clients == null) { return; }
            double now = Timing.TotalTime;
            for (int i = 0; i < clients.Count; i++) {
                Client c = clients[i];
                if (c == null) { continue; }
                RecoveryState st = States.GetOrCreateValue(c);
                if (!st.Init) {
                    st.Init = true;
                    st.LastRecvId = c.LastRecvEntityEventID;
                    st.LastProgressTime = now;
                    st.LastVetoTime = double.NegativeInfinity;
                }
                if (NetIdUtils.IdMoreRecent(c.LastRecvEntityEventID, st.LastRecvId)) {
                    st.LastRecvId = c.LastRecvEntityEventID;
                    st.LastProgressTime = now;
                }
                if (!c.NeedsMidRoundSync && now - st.LastVetoTime > SyncRecoveryPolicy.HealthyResetSeconds) {
                    st.RecoveryStartTime = 0.0;
                }
                if (c.NeedsMidRoundSync) {
                    if (st.RecoveryStartTime == 0.0) { st.RecoveryStartTime = now; }
                    if (SyncRecoveryPolicy.ShouldVeto(now, st.LastProgressTime, st.RecoveryStartTime) &&
                        c.MidRoundSyncTimeOut < now + SyncRecoveryPolicy.SyncExtendSeconds) {
                        c.MidRoundSyncTimeOut = now + SyncRecoveryPolicy.SyncExtendSeconds;
                    }
                }
            }
        }

        internal static bool VetoDesyncDisconnect(GameServer __instance, Client client, PeerDisconnectPacket peerDisconnectPacket) {
            if (client == null) { return true; }
            DisconnectReason reason = peerDisconnectPacket.DisconnectReason;
            // P2PServerPeer cannot disconnect its owner. The native method mutates
            // the client (including removing it from ConnectedClients) before
            // reaching that precondition; rejecting it here keeps the server valid.
            if (__instance.OwnerConnection != null && client.Connection == __instance.OwnerConnection) {
                RecoveryState ownerState = States.GetOrCreateValue(client);
                double ownerTime = Timing.TotalTime;
                if (ownerTime >= ownerState.NextOwnerWarning) {
                    ownerState.NextOwnerWarning = ownerTime + 5.0;
                    PrimaryPackage.Core.ModLog.Error(
                        $"Cannot disconnect the server owner ({reason}); connection and character were preserved. " +
                        "If entity synchronization does not recover, restart the round.");
                }
                return false;
            }
            // An old retained event can still be delivered to a progressing peer.
            // A removed event cannot: do not turn that failure into an endless wait.
            if (reason != DisconnectReason.SyncTimeout && reason != DisconnectReason.ExcessiveDesyncOldEvent) {
                return true;
            }
            double now = Timing.TotalTime;
            RecoveryState st = States.GetOrCreateValue(client);
            if (!st.Init) {
                st.Init = true;
                st.LastRecvId = client.LastRecvEntityEventID;
                st.LastProgressTime = now;
                st.LastVetoTime = double.NegativeInfinity;
            }
            if (st.RecoveryStartTime == 0.0) { st.RecoveryStartTime = now; }
            if (SyncRecoveryPolicy.ShouldVeto(now, st.LastProgressTime, st.RecoveryStartTime)) {
                st.LastVetoTime = now;
                Vetoes++;
                return false;
            }
            return true;
        }
    }
}
