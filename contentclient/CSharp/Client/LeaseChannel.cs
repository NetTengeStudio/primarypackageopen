using System;
using Barotrauma;
using Barotrauma.Networking;
using SubSeal.Wire;

namespace SubSeal.Client
{
    internal sealed class LeaseChannel
    {
        private const int MaxPublicKeyBytes = 256;

        private readonly SessionKey sessionKey;
        private readonly LeaseStore leases;

        internal LeaseChannel(SessionKey sessionKey, LeaseStore leases)
        {
            this.sessionKey = sessionKey;
            this.leases = leases;
        }

        internal void Listen()
        {
            LuaCsSetup.Instance.Networking.Receive(Protocol.ChannelToClient, (IReadMessage message) =>
            {
                try
                {
                    switch (message.ReadByte())
                    {
                        case Protocol.ToClientHelloAck:
                            ReadHelloAck(message);
                            break;
                        case Protocol.ToClientLeaseGrant:
                            ReadLeaseGrant(message);
                            break;
                        case Protocol.ToClientRevoke:
                            leases.Release(message.ReadUInt32());
                            break;
                    }
                }
                catch (Exception exception) when (exception is IndexOutOfRangeException ||
                    exception is ArgumentException || exception is InvalidOperationException ||
                    exception is OverflowException)
                {
                    sessionKey.Discard();
                    leases.ReleaseAll();
                }
            });
        }

        internal void SendHello(byte capabilities)
        {
            byte[] publicKey = sessionKey.BeginExchange();

            IWriteMessage message = LuaCsSetup.Instance.Networking.Start(Protocol.ChannelToServer);
            message.WriteByte(Protocol.ToServerHello);
            message.WriteBytes(Protocol.ContractHash.ToArray(), 0, Protocol.ContractHashLength);
            message.WriteByte(capabilities);
            message.WriteVariableUInt32((uint)publicKey.Length);
            message.WriteBytes(publicKey, 0, publicKey.Length);
            LuaCsSetup.Instance.Networking.Send(message);
        }

        private void ReadHelloAck(IReadMessage message)
        {
            byte policy = message.ReadByte();
            byte[] salt = message.ReadBytes(Protocol.SaltLength);
            byte sessionId = message.ReadByte();
            byte[] confirmation = message.ReadBytes(Protocol.ConfirmLength);
            int publicKeyLength = (int)message.ReadVariableUInt32();
            if (publicKeyLength <= 0 || publicKeyLength > MaxPublicKeyBytes) { return; }

            byte[] serverPublicKey = message.ReadBytes(publicKeyLength);

            if (sessionKey.CompleteExchange(serverPublicKey, salt, sessionId, confirmation))
            {
                PreviewLock.SetPolicy(policy);
            }
        }

        private void ReadLeaseGrant(IReadMessage message)
        {
            uint leaseId = message.ReadUInt32();
            byte[] wrapped = message.ReadBytes(TransferSeal.WrappedKeyLength);

            if (!sessionKey.Established) { return; }

            Span<byte> contentKey = stackalloc byte[Protocol.KeyLength];
            if (!TransferSeal.TryUnwrapKey(wrapped, sessionKey.Key, leaseId, contentKey)) { return; }

            leases.Hold(leaseId, contentKey);
            contentKey.Clear();

            IWriteMessage acknowledgement = LuaCsSetup.Instance.Networking.Start(Protocol.ChannelToServer);
            acknowledgement.WriteByte(Protocol.ToServerLeaseAck);
            acknowledgement.WriteUInt32(leaseId);
            LuaCsSetup.Instance.Networking.Send(acknowledgement);
        }
    }
}
