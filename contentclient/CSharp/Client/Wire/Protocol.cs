using System;
using System.Security.Cryptography;
using System.Text;

namespace SubSeal.Wire
{
    internal static class Protocol
    {
        internal const byte Version = 4;

        internal const int MagicLength = 4;
        internal const int FlagsLength = 1;
        internal const int NonceLength = 12;
        internal const int LeaseIdLength = 4;
        internal const int TagLength = 16;
        internal const int KeyLength = 32;
        internal const int SaltLength = 16;
        internal const int ContractHashLength = 32;
        internal const int ConfirmLength = 16;

        internal const int HeaderLength = MagicLength + FlagsLength + NonceLength + LeaseIdLength;
        internal const int SealOverhead = HeaderLength + TagLength;

        internal const int FlagsOffset = MagicLength;
        internal const int NonceOffset = FlagsOffset + FlagsLength;
        internal const int LeaseIdOffset = NonceOffset + NonceLength;
        internal const int PayloadOffset = HeaderLength;

        internal const int VersionShift = 0;
        internal const int VersionMask = 0x0F;
        internal const int KindShift = 4;
        internal const int KindMask = 0x03;

        internal const byte KindSubmarine = 0;
        internal const byte KindSaveEntry = 1;

        internal const string ChannelToServer = "subseal.c2s";
        internal const string ChannelToClient = "subseal.s2c";

        internal const byte ToServerHello = 1;
        internal const byte ToServerLeaseAck = 2;

        internal const byte ToClientHelloAck = 1;
        internal const byte ToClientLeaseGrant = 2;
        internal const byte ToClientRevoke = 3;

        internal const uint AllLeases = 0;

        internal const byte CapabilityCanOpenSealed = 1 << 0;

        internal const byte PolicyPreviewProtected = 1 << 0;

        private const string ConfirmLabel = "subseal-confirm";

        private const int DeclaredLayoutLength = MagicLength + 26;

        private const int LabelLength = 11 + 11 + 15;

        private static readonly byte[] MagicBytes = { 0xA7, 0x53, 0x42, 0x4C };

        private static byte[] contractHash;

        internal static ReadOnlySpan<byte> Magic => MagicBytes;

        internal static ReadOnlySpan<byte> ContractHash => contractHash ??= ComputeContractHash();

        internal static void DeriveConfirmation(ReadOnlySpan<byte> sessionKey, Span<byte> destination)
        {
            Span<byte> label = stackalloc byte[ConfirmLabel.Length + ContractHashLength];
            int written = Encoding.UTF8.GetBytes(ConfirmLabel, label);
            ContractHash.CopyTo(label.Slice(written));

            Span<byte> full = stackalloc byte[ContractHashLength];
            HMACSHA256.HashData(sessionKey, label.Slice(0, written + ContractHashLength), full);
            full.Slice(0, ConfirmLength).CopyTo(destination);
            CryptographicOperations.ZeroMemory(full);
        }

        private static byte[] ComputeContractHash()
        {
            Span<byte> layout = stackalloc byte[DeclaredLayoutLength + LabelLength + TransferSeal.ContractVectorLength];
            int i = 0;

            MagicBytes.CopyTo(layout);
            i += MagicLength;

            layout[i++] = Version;
            layout[i++] = MagicLength;
            layout[i++] = FlagsLength;
            layout[i++] = NonceLength;
            layout[i++] = LeaseIdLength;
            layout[i++] = TagLength;
            layout[i++] = KeyLength;
            layout[i++] = SaltLength;
            layout[i++] = ContractHashLength;
            layout[i++] = ConfirmLength;
            layout[i++] = HeaderLength;
            layout[i++] = SealOverhead;
            layout[i++] = VersionShift;
            layout[i++] = VersionMask;
            layout[i++] = KindShift;
            layout[i++] = KindMask;
            layout[i++] = KindSubmarine;
            layout[i++] = KindSaveEntry;
            layout[i++] = ToServerHello;
            layout[i++] = ToServerLeaseAck;
            layout[i++] = ToClientHelloAck;
            layout[i++] = ToClientLeaseGrant;
            layout[i++] = ToClientRevoke;
            layout[i++] = (byte)AllLeases;
            layout[i++] = CapabilityCanOpenSealed;
            layout[i++] = PolicyPreviewProtected;

            i += Encoding.UTF8.GetBytes(ChannelToServer, layout.Slice(i));
            i += Encoding.UTF8.GetBytes(ChannelToClient, layout.Slice(i));
            i += Encoding.UTF8.GetBytes(ConfirmLabel, layout.Slice(i));

            i += TransferSeal.WriteContractVectors(layout.Slice(i));

            byte[] digest = new byte[ContractHashLength];
            SHA256.HashData(layout.Slice(0, i), digest);
            return digest;
        }
    }
}
