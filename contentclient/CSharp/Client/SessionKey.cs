using System;
using System.Security.Cryptography;
using SubSeal.Wire;

namespace SubSeal.Client
{
    internal sealed class SessionKey
    {
        private const int ExpectedKeySizeBits = 256;

        private ECDiffieHellman local;
        private byte[] key;

        internal bool Established => key is not null;

        internal ReadOnlySpan<byte> Key => key;

        internal byte[] BeginExchange()
        {
            if (local is not null && key is null) { return local.ExportSubjectPublicKeyInfo(); }
            Discard();
            local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            return local.ExportSubjectPublicKeyInfo();
        }

        internal bool CompleteExchange(ReadOnlySpan<byte> serverPublicKey, ReadOnlySpan<byte> salt, byte sessionId, ReadOnlySpan<byte> confirmation)
        {
            if (local is null) { return false; }

            byte[] candidate;

            try
            {
                using ECDiffieHellman remote = ECDiffieHellman.Create();
                remote.ImportSubjectPublicKeyInfo(serverPublicKey, out _);
                if (remote.KeySize != ExpectedKeySizeBits) { return false; }

                byte[] shared = local.DeriveRawSecretAgreement(remote.PublicKey);
                candidate = TransferSeal.DeriveSessionKey(shared, salt, sessionId);
                CryptographicOperations.ZeroMemory(shared);
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            Span<byte> expected = stackalloc byte[Protocol.ConfirmLength];
            Protocol.DeriveConfirmation(candidate, expected);

            if (!CryptographicOperations.FixedTimeEquals(expected, confirmation))
            {
                CryptographicOperations.ZeroMemory(candidate);
                return false;
            }

            key = candidate;
            local.Dispose();
            local = null;
            return true;
        }

        internal void Discard()
        {
            local?.Dispose();
            local = null;
            if (key is null) { return; }
            CryptographicOperations.ZeroMemory(key);
            key = null;
        }
    }
}
