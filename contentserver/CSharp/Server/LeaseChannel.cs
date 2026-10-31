using System;
using Barotrauma;
using Barotrauma.Networking;
using SubSeal.Wire;

namespace SubSeal.Server
{
    internal sealed class LeaseChannel
    {
        private const int MaxPublicKeyBytes = 256;
        private const int MaxHandshakesPerWindow = 5;
        private const int HandshakeWindowSeconds = 10;

        private readonly SessionKeys sessions;
        private readonly Leases leases;
        private readonly bool previewProtection;
        private bool malformedReported;

        private readonly RateLimiter handshakeLimit = new RateLimiter(
            maxRequests: MaxHandshakesPerWindow,
            expiryInSeconds: HandshakeWindowSeconds,
            punishmentRules: new[]
            {
                (RateLimitAction.OnLimitReached, RateLimitPunishment.Announce)
            });

        internal LeaseChannel(SessionKeys sessions, Leases leases, bool previewProtection)
        {
            this.sessions = sessions;
            this.leases = leases;
            this.previewProtection = previewProtection;
        }

        internal void Listen()
        {
            LuaCsSetup.Instance.Networking.Receive(Protocol.ChannelToServer, (IReadMessage message, Client client) =>
            {
                if (client?.Connection is null) { return; }

                try
                {
                    switch (message.ReadByte())
                    {
                        case Protocol.ToServerHello:
                            if (handshakeLimit.IsLimitReached(client)) { return; }
                            ReadHello(message, client);
                            break;
                        case Protocol.ToServerLeaseAck:
                            ReadLeaseAck(message, client);
                            break;
                    }
                }
                catch (Exception exception) when (exception is IndexOutOfRangeException ||
                    exception is ArgumentException || exception is InvalidOperationException ||
                    exception is OverflowException)
                {
                    if (malformedReported) { return; }
                    malformedReported = true;
                    Diagnostics.Fault($"malformed SubSeal channel message: {exception.GetType().Name}");
                }
            });
        }

        internal void SendGrant(Leases.Lease lease, ReadOnlySpan<byte> sessionKey)
        {
            Span<byte> wrapped = stackalloc byte[TransferSeal.WrappedKeyLength];
            TransferSeal.WrapKey(lease.ContentKey, sessionKey, lease.Id, wrapped);

            IWriteMessage message = LuaCsSetup.Instance.Networking.Start(Protocol.ChannelToClient);
            message.WriteByte(Protocol.ToClientLeaseGrant);
            message.WriteUInt32(lease.Id);
            message.WriteBytes(wrapped.ToArray(), 0, TransferSeal.WrappedKeyLength);
            LuaCsSetup.Instance.Networking.Send(message, lease.Connection);
        }

        internal void SendRevoke(NetworkConnection connection, uint leaseId)
        {
            if (connection is null) { return; }

            IWriteMessage message = LuaCsSetup.Instance.Networking.Start(Protocol.ChannelToClient);
            message.WriteByte(Protocol.ToClientRevoke);
            message.WriteUInt32(leaseId);
            LuaCsSetup.Instance.Networking.Send(message, connection);
        }

        private void ReadLeaseAck(IReadMessage message, Client client)
        {
            uint id = message.ReadUInt32();
            Leases.Lease lease = leases.Arm(client.Connection, id);

            if (lease is null)
            {
                
                Diagnostics.Note($"{client.Name} lease {id} not armable");
                return;
            }

            lease.LastRetryAt = Timing.TotalTime;
            TransferPatches.Retry(lease.Connection, lease.FileType, lease.FilePath);
        }

        private void ReadHello(IReadMessage message, Client client)
        {
            byte[] contractHash = message.ReadBytes(Protocol.ContractHashLength);
            byte capabilities = message.ReadByte();
            int publicKeyLength = (int)message.ReadVariableUInt32();

            if (publicKeyLength <= 0 || publicKeyLength > MaxPublicKeyBytes) { return; }
            if (!Protocol.ContractHash.SequenceEqual(contractHash))
            {
                Diagnostics.Fault($"{client.Name} contract mismatch");
                return;
            }

            byte[] clientPublicKey = message.ReadBytes(publicKeyLength);

            Span<byte> serverPublicKey = stackalloc byte[MaxPublicKeyBytes];
            Span<byte> salt = stackalloc byte[Protocol.SaltLength];

            SessionKeys.Session session = sessions.Establish(
                client.Connection, client.SessionId, capabilities, clientPublicKey, serverPublicKey, out int serverPublicKeyLength, salt, out bool reused);

            if (session is null)
            {
                Diagnostics.Fault($"{client.Name} key exchange rejected");
                return;
            }

            if (!reused) { leases.Revoke(client.Connection); }

            Span<byte> confirmation = stackalloc byte[Protocol.ConfirmLength];
            Protocol.DeriveConfirmation(session.Key, confirmation);

            IWriteMessage reply = LuaCsSetup.Instance.Networking.Start(Protocol.ChannelToClient);
            reply.WriteByte(Protocol.ToClientHelloAck);
            reply.WriteByte(previewProtection ? Protocol.PolicyPreviewProtected : (byte)0);
            reply.WriteBytes(salt.ToArray(), 0, Protocol.SaltLength);
            reply.WriteByte(session.SessionId);
            reply.WriteBytes(confirmation.ToArray(), 0, Protocol.ConfirmLength);
            reply.WriteVariableUInt32((uint)serverPublicKeyLength);
            reply.WriteBytes(serverPublicKey.Slice(0, serverPublicKeyLength).ToArray(), 0, serverPublicKeyLength);
            LuaCsSetup.Instance.Networking.Send(reply, client.Connection);
            TransferPatches.FlushDeferred(client.Connection);
        }
    }
}
