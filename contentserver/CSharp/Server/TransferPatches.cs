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

            foreach (Leases.Lease lease in leases.All)
            {
                if (!lease.Armed || lease.Consumed || Timing.TotalTime - lease.LastRetryAt < RegrantIntervalSeconds) { continue; }

                int recipientTransfers = 0;
                foreach (FileSender.FileTransferOut transfer in sender.ActiveTransfers)
                {
                    if (transfer.Connection == lease.Connection) { recipientTransfers++; }
                }
                if (sender.ActiveTransfers.Count >= 16 || recipientTransfers >= 5) { continue; }

                lease.LastRetryAt = Timing.TotalTime;
                Retry(lease.Connection, lease.FileType, lease.FilePath);
            }
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

            if (recipient is null) { return false; }
            if (recipient == GameMain.Server?.OwnerConnection) { return true; }
            if (!ModTransferPolicy.Allows(fileType, filePath))
            {
                Diagnostics.Note($"{Diagnostics.Describe(recipient)} refused mod archive {Name(filePath)}");
                return false;
            }

            if (fileType != FileTransferType.Submarine && fileType != FileTransferType.CampaignSave) { return true; }
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
                // A peer's capability bit is self-reported, never proof of integrity.
                // Compatibility mode is forbidden when preview protection is active.
                if (settings.PreviewProtection || !settings.AllowUnsealedForIncapableClients)
                {
                    Diagnostics.Note($"{Name(filePath)} refused");
                    return false;
                }

                if (!entitlement.Allows(recipient, fileType, filePath, out Claim unsealedClaim))
                {
                    Diagnostics.Note($"{Diagnostics.Describe(recipient)} not entitled to {Name(filePath)} ({unsealedClaim})");
                    return false;
                }

                Diagnostics.Note($"{Name(filePath)} sent unsealed under explicit compatibility policy");
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

            try
            {
                byte[] plain = __result.Data;
                if (plain is null) { throw new System.IO.InvalidDataException("transfer has no source data"); }

                byte[] output = __state.FileType == FileTransferType.CampaignSave
                    ? SaveResealer.Reseal(plain, __state.ContentKey, __state.Id)
                    : SealSubmarine(plain, __state);

                if (output is null) { throw new System.IO.InvalidDataException("sealing returned no output"); }

                EngineSymbols.OverwriteData(__result, output);
                if (!object.ReferenceEquals(__result.Data, output))
                {
                    throw new System.InvalidOperationException("transfer payload replacement failed");
                }
            }
            catch (System.Exception exception)
            {
                // FileSender has already enqueued the plaintext transfer at this point.
                // Error status causes FileSender.Update to discard it before Send().
                __result.Status = FileTransferStatus.Error;
                Diagnostics.Fault($"{Name(__state.FilePath)} lease {__state.Id} seal aborted: {exception.GetType().Name}");
            }
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
