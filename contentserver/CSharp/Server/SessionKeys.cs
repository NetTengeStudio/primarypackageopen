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
            internal byte[] ClientPublicKey;
            internal byte[] ServerPublicKey;
            internal byte[] Salt;
        }

        private readonly Dictionary<NetworkConnection, Session> sessions = new Dictionary<NetworkConnection, Session>();

        internal Session Find(NetworkConnection connection)
            => connection is not null && sessions.TryGetValue(connection, out Session session) ? session : null;

        internal Session Establish(NetworkConnection connection, byte sessionId, byte capabilities, ReadOnlySpan<byte> clientPublicKey, Span<byte> serverPublicKey, out int serverPublicKeyLength, Span<byte> salt, out bool reused)
        {
            serverPublicKeyLength = 0;
            reused = false;
            if (sessions.TryGetValue(connection, out Session previous) &&
                previous.SessionId == sessionId && previous.Capabilities == capabilities &&
                clientPublicKey.SequenceEqual(previous.ClientPublicKey))
            {
                previous.ServerPublicKey.AsSpan().CopyTo(serverPublicKey);
                previous.Salt.AsSpan().CopyTo(salt);
                serverPublicKeyLength = previous.ServerPublicKey.Length;
                reused = true;
                return previous;
            }
            Drop(connection);

            using ECDiffieHellman local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using ECDiffieHellman remote = ECDiffieHellman.Create();

            byte[] shared;

            try
            {
                remote.ImportSubjectPublicKeyInfo(clientPublicKey, out _);
                if (remote.KeySize != ExpectedKeySizeBits) { return null; }

                if (!local.TryExportSubjectPublicKeyInfo(serverPublicKey, out serverPublicKeyLength)) { return null; }

                RandomNumberGenerator.Fill(salt);
                shared = local.DeriveRawSecretAgreement(remote.PublicKey);
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

            Session session = new Session
            {
                Key = TransferSeal.DeriveSessionKey(shared, salt, sessionId),
                Capabilities = capabilities,
                SessionId = sessionId,
                ClientPublicKey = clientPublicKey.ToArray(),
                ServerPublicKey = serverPublicKey.Slice(0, serverPublicKeyLength).ToArray(),
                Salt = salt.ToArray()
            };
            CryptographicOperations.ZeroMemory(shared);

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
