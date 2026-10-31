using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SubSeal.Wire
{
    internal static class TransferSeal
    {
        internal const int WrappedKeyLength = Protocol.NonceLength + Protocol.KeyLength + Protocol.TagLength;

        private static readonly byte[] VectorKey =
        {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F
        };

        private static readonly byte[] VectorNonce =
        {
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB
        };

        private static readonly byte[] VectorPlain =
        {
            0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47,
            0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F
        };

        private static readonly byte[] VectorSalt =
        {
            0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57,
            0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F
        };

        private static readonly byte[] VectorInfo = { 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67 };

        private const uint VectorLeaseId = 0x11223344;

        private const int VectorPlainLength = 16;

        internal const int ContractVectorLength =
            Protocol.SealOverhead + VectorPlainLength + WrappedKeyLength + Protocol.KeyLength;

        internal static int SealedLength(int plainLength) => plainLength + Protocol.SealOverhead;

        internal static int PlainLength(int sealedLength) => sealedLength - Protocol.SealOverhead;

        internal static bool IsSealed(ReadOnlySpan<byte> buffer)
            => buffer.Length >= Protocol.SealOverhead && CarriesHeader(buffer);

        internal static bool CarriesHeader(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < Protocol.HeaderLength) { return false; }
            if (!buffer.Slice(0, Protocol.MagicLength).SequenceEqual(Protocol.Magic)) { return false; }
            return ((buffer[Protocol.FlagsOffset] >> Protocol.VersionShift) & Protocol.VersionMask) == Protocol.Version;
        }

        internal static uint ReadLeaseId(ReadOnlySpan<byte> buffer)
            => BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(Protocol.LeaseIdOffset, Protocol.LeaseIdLength));

        internal static byte ReadKind(ReadOnlySpan<byte> buffer)
            => (byte)((buffer[Protocol.FlagsOffset] >> Protocol.KindShift) & Protocol.KindMask);

        internal static void Seal(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, uint leaseId, byte kind, Span<byte> destination)
        {
            Span<byte> nonce = stackalloc byte[Protocol.NonceLength];
            RandomNumberGenerator.Fill(nonce);
            SealWith(plain, key, leaseId, kind, nonce, destination);
        }

        private static void SealWith(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, uint leaseId, byte kind, ReadOnlySpan<byte> nonce, Span<byte> destination)
        {
            Protocol.Magic.CopyTo(destination);
            destination[Protocol.FlagsOffset] = (byte)
                (((Protocol.Version & Protocol.VersionMask) << Protocol.VersionShift)
                 | ((kind & Protocol.KindMask) << Protocol.KindShift));
            nonce.CopyTo(destination.Slice(Protocol.NonceOffset, Protocol.NonceLength));
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(Protocol.LeaseIdOffset, Protocol.LeaseIdLength), leaseId);

            using AesGcm cipher = new AesGcm(key, Protocol.TagLength);
            cipher.Encrypt(
                destination.Slice(Protocol.NonceOffset, Protocol.NonceLength),
                plain,
                destination.Slice(Protocol.PayloadOffset, plain.Length),
                destination.Slice(Protocol.PayloadOffset + plain.Length, Protocol.TagLength),
                destination.Slice(0, Protocol.HeaderLength));
        }

        internal static bool TryOpen(ReadOnlySpan<byte> sealedBuffer, ReadOnlySpan<byte> key, Span<byte> destination, out int plainLength)
        {
            plainLength = 0;
            if (!IsSealed(sealedBuffer)) { return false; }

            int length = PlainLength(sealedBuffer.Length);
            if (length < 0 || destination.Length < length) { return false; }

            try
            {
                using AesGcm cipher = new AesGcm(key, Protocol.TagLength);
                cipher.Decrypt(
                    sealedBuffer.Slice(Protocol.NonceOffset, Protocol.NonceLength),
                    sealedBuffer.Slice(Protocol.PayloadOffset, length),
                    sealedBuffer.Slice(Protocol.PayloadOffset + length, Protocol.TagLength),
                    destination.Slice(0, length),
                    sealedBuffer.Slice(0, Protocol.HeaderLength));
            }
            catch (CryptographicException)
            {
                return false;
            }

            plainLength = length;
            return true;
        }

        internal static void WrapKey(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> sessionKey, uint leaseId, Span<byte> destination)
        {
            Span<byte> nonce = stackalloc byte[Protocol.NonceLength];
            RandomNumberGenerator.Fill(nonce);
            WrapKeyWith(contentKey, sessionKey, leaseId, nonce, destination);
        }

        private static void WrapKeyWith(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> sessionKey, uint leaseId, ReadOnlySpan<byte> nonce, Span<byte> destination)
        {
            Span<byte> associated = stackalloc byte[Protocol.LeaseIdLength];
            BinaryPrimitives.WriteUInt32LittleEndian(associated, leaseId);

            nonce.CopyTo(destination.Slice(0, Protocol.NonceLength));

            using AesGcm cipher = new AesGcm(sessionKey, Protocol.TagLength);
            cipher.Encrypt(
                destination.Slice(0, Protocol.NonceLength),
                contentKey,
                destination.Slice(Protocol.NonceLength, Protocol.KeyLength),
                destination.Slice(Protocol.NonceLength + Protocol.KeyLength, Protocol.TagLength),
                associated);
        }

        internal static bool TryUnwrapKey(ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> sessionKey, uint leaseId, Span<byte> destination)
        {
            if (wrapped.Length != WrappedKeyLength || destination.Length < Protocol.KeyLength) { return false; }

            Span<byte> associated = stackalloc byte[Protocol.LeaseIdLength];
            BinaryPrimitives.WriteUInt32LittleEndian(associated, leaseId);

            try
            {
                using AesGcm cipher = new AesGcm(sessionKey, Protocol.TagLength);
                cipher.Decrypt(
                    wrapped.Slice(0, Protocol.NonceLength),
                    wrapped.Slice(Protocol.NonceLength, Protocol.KeyLength),
                    wrapped.Slice(Protocol.NonceLength + Protocol.KeyLength, Protocol.TagLength),
                    destination.Slice(0, Protocol.KeyLength),
                    associated);
            }
            catch (CryptographicException)
            {
                return false;
            }

            return true;
        }

        internal static byte[] DeriveSessionKey(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> salt, byte sessionId)
        {
            Span<byte> info = stackalloc byte[Protocol.ContractHashLength + 1];
            Protocol.ContractHash.CopyTo(info);
            info[Protocol.ContractHashLength] = sessionId;

            byte[] key = new byte[Protocol.KeyLength];
            DeriveKeyMaterial(sharedSecret, salt, info, key);
            return key;
        }

        private static void DeriveKeyMaterial(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, Span<byte> destination)
            => HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, destination, salt, info);

        internal static int WriteContractVectors(Span<byte> destination)
        {
            int written = 0;

            Span<byte> sealedVector = stackalloc byte[SealedLength(VectorPlainLength)];
            SealWith(VectorPlain, VectorKey, VectorLeaseId, Protocol.KindSaveEntry, VectorNonce, sealedVector);
            sealedVector.CopyTo(destination);
            written += sealedVector.Length;

            Span<byte> wrappedVector = stackalloc byte[WrappedKeyLength];
            WrapKeyWith(VectorKey, VectorKey, VectorLeaseId, VectorNonce, wrappedVector);
            wrappedVector.CopyTo(destination.Slice(written));
            written += WrappedKeyLength;

            Span<byte> derivedVector = stackalloc byte[Protocol.KeyLength];
            DeriveKeyMaterial(VectorKey, VectorSalt, VectorInfo, derivedVector);
            derivedVector.CopyTo(destination.Slice(written));
            written += Protocol.KeyLength;

            return written;
        }
    }
}
