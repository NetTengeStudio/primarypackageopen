using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using SubSeal.Wire;

namespace SubSeal.Server
{
    internal static class TransferPatches
    {
        
        private const double RegrantIntervalSeconds = 2.0;
        private const int MaxDeferredPerConnection = 16;

        private readonly struct DeferredTransfer
        {
            internal readonly FileTransferType Type;
            internal readonly string Path;

            internal DeferredTransfer(FileTransferType type, string path)
            {
                Type = type;
                Path = path;
            }
        }

        private static readonly Dictionary<NetworkConnection, List<DeferredTransfer>> deferred = new();
        private static readonly List<uint> retryCandidates = new();
        private static readonly Dictionary<NetworkConnection, int> activeByConnection =
            new(ReferenceEqualityComparer.Instance);

        private static Settings settings;
        private static SessionKeys sessions;
        private static Leases leases;
        private static Entitlement entitlement;
        private static LeaseChannel channel;

        internal static void Bind(Settings settings, SessionKeys sessions, Leases leases, Entitlement entitlement, LeaseChannel channel)
        {
            TransferPatches.settings = settings;
            TransferPatches.sessions = sessions;
            TransferPatches.leases = leases;
            TransferPatches.entitlement = entitlement;
            TransferPatches.channel = channel;
        }

        internal static void Unbind()
        {
            deferred.Clear();
            retryCandidates.Clear();
            activeByConnection.Clear();
            settings = null;
            sessions = null;
            leases = null;
            entitlement = null;
            channel = null;
        }

        internal static void Retry(NetworkConnection recipient, FileTransferType fileType, string filePath)
            => GameMain.Server?.FileSender?.StartTransfer(recipient, fileType, filePath);

        internal static void FlushDeferred(NetworkConnection recipient)
        {
            if (!deferred.Remove(recipient, out List<DeferredTransfer> transfers)) { return; }
            foreach (DeferredTransfer transfer in transfers)
            {
                Retry(recipient, transfer.Type, transfer.Path);
            }
        }

        internal static void DropDeferred(NetworkConnection recipient) => deferred.Remove(recipient);

        internal static void RetryArmed(Leases leases)
        {
            FileSender sender = GameMain.Server?.FileSender;
            if (sender is null) { return; }

            // One snapshot for the entire retry pass, not a scan of all network
            // transfers for every armed lease. Callbacks may consume leases during Retry.
            leases.CaptureRetryCandidates(retryCandidates);
            activeByConnection.Clear();
            foreach (FileSender.FileTransferOut transfer in sender.ActiveTransfers)
            {
                if (transfer.Connection == null) { continue; }
                activeByConnection.TryGetValue(transfer.Connection, out int count);
                activeByConnection[transfer.Connection] = count + 1;
            }

            for (int i = 0; i < retryCandidates.Count; i++)
            {
                Leases.Lease lease = leases.FindRetryCandidate(retryCandidates[i]);
                if (lease == null || Timing.TotalTime - lease.LastRetryAt < RegrantIntervalSeconds) { continue; }
                activeByConnection.TryGetValue(lease.Connection, out int recipientTransfers);
                if (sender.ActiveTransfers.Count >= 16 || recipientTransfers >= 5) { continue; }

                lease.LastRetryAt = Timing.TotalTime;
                int previousCount = sender.ActiveTransfers.Count;
                Retry(lease.Connection, lease.FileType, lease.FilePath);
                // FileSender.StartTransfer appends the transfer synchronously.
                // Reflect only actual successes; a rejected or missing file adds nothing.
                int added = sender.ActiveTransfers.Count - previousCount;
                if (added > 0) { activeByConnection[lease.Connection] = recipientTransfers + added; }
            }

            retryCandidates.Clear();
            activeByConnection.Clear();
        }

        private static void Defer(NetworkConnection recipient, FileTransferType type, string path)
        {
            if (!deferred.TryGetValue(recipient, out List<DeferredTransfer> transfers))
            {
                transfers = new List<DeferredTransfer>();
                deferred.Add(recipient, transfers);
            }

            foreach (DeferredTransfer transfer in transfers)
            {
                if (transfer.Type == type && transfer.Path == path) { return; }
            }

            if (transfers.Count < MaxDeferredPerConnection)
            {
                transfers.Add(new DeferredTransfer(type, path));
            }
        }

        public static bool BeforeStartTransfer(NetworkConnection recipient, FileTransferType fileType, string filePath, ref Leases.Lease __state)
        {
            __state = null;

            if (fileType != FileTransferType.Submarine && fileType != FileTransferType.CampaignSave) { return true; }
            if (recipient is null || recipient == GameMain.Server.OwnerConnection) { return true; }

            if (leases is null || sessions is null || channel is null || entitlement is null) { return false; }

            SessionKeys.Session session = sessions.Find(recipient);
            bool capable = session is not null && (session.Capabilities & Protocol.CapabilityCanOpenSealed) != 0;

            if (!capable)
            {
                if (session is null)
                {
                    Defer(recipient, fileType, filePath);
                    return false;
                }
                Diagnostics.Note($"{Diagnostics.Describe(recipient)} cannot open sealed content");
                if (!settings.AllowUnsealedForIncapableClients)
                {
                    Diagnostics.Note($"{Name(filePath)} refused");
                    return false;
                }
                Diagnostics.Note($"{Name(filePath)} sent unsealed");
                return true;
            }

            Leases.Lease pending = leases.FindPending(recipient, fileType, filePath);
            if (pending is not null)
            {
                if (pending.Armed)
                {
                    __state = pending;
                    return true;
                }

                if (Timing.TotalTime - pending.LastGrantAt >= RegrantIntervalSeconds)
                {
                    channel.SendGrant(pending, session.Key);
                    leases.MarkGranted(pending);
                    Diagnostics.Note($"{Diagnostics.Describe(recipient)} lease {pending.Id} regranted");
                }
                else
                {
                    Diagnostics.Note($"{Diagnostics.Describe(recipient)} lease {pending.Id} awaiting ack");
                }

                return false;
            }

            if (!entitlement.Allows(recipient, fileType, filePath, out Claim claim))
            {
                Diagnostics.Note($"{Diagnostics.Describe(recipient)} not entitled to {Name(filePath)} ({claim})");
                return false;
            }

            Leases.Lease issued = leases.Issue(recipient, fileType, filePath);
            if (issued is null)
            {
                Diagnostics.Note($"{Diagnostics.Describe(recipient)} holds too many leases");
                return false;
            }

            channel.SendGrant(issued, session.Key);
            leases.MarkGranted(issued);
            Diagnostics.Note($"{Diagnostics.Describe(recipient)} granted lease {issued.Id} for {Name(filePath)}");
            return false;
        }

        public static void AfterStartTransfer(FileSender.FileTransferOut __result, Leases.Lease __state)
        {
            if (__state is null || leases is null) { return; }

            if (__result is null)
            {
                Diagnostics.Note($"{Name(__state.FilePath)} lease {__state.Id} not started");
                return;
            }

            leases.MarkConsumed(__state);

            byte[] plain = __result.Data;
            byte[] output = __state.FileType == FileTransferType.CampaignSave
                ? SaveResealer.Reseal(plain, __state.ContentKey, __state.Id)
                : SealSubmarine(plain, __state);

            if (output is null)
            {
                __result.Status = FileTransferStatus.Error;
                Diagnostics.Fault($"{Name(__state.FilePath)} lease {__state.Id} seal aborted");
                return;
            }

            EngineSymbols.OverwriteData(__result, output);
        }

        private static byte[] SealSubmarine(byte[] plain, Leases.Lease lease)
        {
            byte[] output = new byte[TransferSeal.SealedLength(plain.Length)];
            TransferSeal.Seal(plain, lease.ContentKey, lease.Id, Protocol.KindSubmarine, output);
            return output;
        }

        private static string Name(string filePath) => System.IO.Path.GetFileName(filePath);
    }
}
