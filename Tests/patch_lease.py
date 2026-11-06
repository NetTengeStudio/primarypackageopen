import os

path = r'C:\Steam\steamapps\common\Barotrauma\LocalMods\contentserver\CSharp\Server\LeaseChannel.cs'
with open(path, 'r', encoding='utf-8') as f:
    text = f.read()

old_block = """            if (!Protocol.ContractHash.SequenceEqual(contractHash))
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
            reply.WriteByte(previewProtection ? Protocol.PolicyPreviewProtected : (byte)0);"""

new_block = """            byte clientVersion;
            byte[] activeContractHash;
            if (Protocol.ContractHash.SequenceEqual(contractHash))
            {
                clientVersion = Protocol.Version;
                activeContractHash = Protocol.ContractHash.ToArray();
            }
            else if (Protocol.ContractHashV3.SequenceEqual(contractHash))
            {
                clientVersion = 3;
                activeContractHash = Protocol.ContractHashV3.ToArray();
            }
            else
            {
                Diagnostics.Fault($"{client.Name} contract mismatch");
                return;
            }

            byte[] clientPublicKey = message.ReadBytes(publicKeyLength);

            Span<byte> serverPublicKey = stackalloc byte[MaxPublicKeyBytes];
            Span<byte> salt = stackalloc byte[Protocol.SaltLength];

            SessionKeys.Session session = sessions.Establish(
                client.Connection, client.SessionId, capabilities, clientPublicKey, serverPublicKey, out int serverPublicKeyLength, salt, out bool reused, activeContractHash);

            if (session is null)
            {
                Diagnostics.Fault($"{client.Name} key exchange rejected");
                return;
            }

            if (!reused) { leases.Revoke(client.Connection); }

            Span<byte> confirmation = stackalloc byte[Protocol.ConfirmLength];
            Protocol.DeriveConfirmation(session.Key, confirmation, activeContractHash);

            IWriteMessage reply = LuaCsSetup.Instance.Networking.Start(Protocol.ChannelToClient);
            reply.WriteByte(Protocol.ToClientHelloAck);
            if (clientVersion >= 4)
            {
                reply.WriteByte(previewProtection ? Protocol.PolicyPreviewProtected : (byte)0);
            }"""

assert old_block in text, "old_block not found in LeaseChannel.cs"
text = text.replace(old_block, new_block)
with open(path, 'w', encoding='utf-8') as f:
    f.write(text)
print("LeaseChannel.cs updated successfully")
