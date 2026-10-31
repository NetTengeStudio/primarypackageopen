using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Barotrauma.Networking;
using SubSeal.Wire;

namespace SubSeal.Server
{
    internal sealed class SessionKeys
    {
        private const int ExpectedKeySizeBits = 256;

        internal sealed class Session
        {
            internal byte[] Key;
            internal byte Capabilities;
            internal byte SessionId;
        }

        private readonly Dictionary<NetworkConnection, Session> sessions = new Dictionary<NetworkConnection, Session>();

        internal Session Find(NetworkConnection connection)
            => connection is not null && sessions.TryGetValue(connection, out Session session) ? session : null;

        internal Session Establish(NetworkConnection connection, byte sessionId, byte capabilities,
            ReadOnlySpan<byte> clientPublicKey, Span<byte> serverPublicKey,
            out int serverPublicKeyLength, Span<byte> salt)
        {
            serverPublicKeyLength = 0;
            if (connection is null) { return null; }

            // Derive the new session before mutating the active one.
            // An invalid or unauthenticated Hello must not destroy a valid key.
            using ECDiffieHellman local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using ECDiffieHellman remote = ECDiffieHellman.Create();

            byte[] shared = null;
            byte[] nextKey = null;
            try
            {
                remote.ImportSubjectPublicKeyInfo(clientPublicKey, out int bytesRead);
                if (bytesRead != clientPublicKey.Length || remote.KeySize != ExpectedKeySizeBits)
                {
                    return null;
                }

                if (!local.TryExportSubjectPublicKeyInfo(serverPublicKey, out serverPublicKeyLength))
                {
                    serverPublicKeyLength = 0;
                    return null;
                }

                RandomNumberGenerator.Fill(salt);
                shared = local.DeriveRawSecretAgreement(remote.PublicKey);
                nextKey = TransferSeal.DeriveSessionKey(shared, salt, sessionId);
            }
            catch (CryptographicException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            finally
            {
                if (shared != null) { CryptographicOperations.ZeroMemory(shared); }
            }

            if (nextKey is null) { return null; }

            Session session = new Session
            {
                Key = nextKey,
                Capabilities = capabilities,
                SessionId = sessionId
            };
            Drop(connection);
            sessions[connection] = session;
            return session;
        }

        internal void Drop(NetworkConnection connection)
        {
            if (connection is null || !sessions.TryGetValue(connection, out Session session)) { return; }
            CryptographicOperations.ZeroMemory(session.Key);
            sessions.Remove(connection);
        }

        internal void DropAll()
        {
            foreach (Session session in sessions.Values)
            {
                CryptographicOperations.ZeroMemory(session.Key);
            }
            sessions.Clear();
        }
    }
}
