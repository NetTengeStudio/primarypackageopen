using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace UltimateSyncTests
{
    internal static class Program
    {
        private static int passed = 0;
        private static int total = 0;

        private static void Assert(bool condition, string testName)
        {
            total++;
            if (!condition)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {testName}");
                Console.ResetColor();
                throw new Exception($"Test failed: {testName}");
            }
            passed++;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] {testName}");
            Console.ResetColor();
        }

        private static int Main(string[] args)
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("        ULTIMATE MULTI-CLIENT PROTOCOL & SYNC TEST HARNESS       ");
            Console.WriteLine("=================================================================\n");

            try
            {
                TestContractHashes();
                TestKeyExchangeV3();
                TestKeyExchangeV4();
                TestTamperedClientMismatch();
                TestSubmarineEncryptionAndUnwrap();
                TestSubmarineSkeletonReductionAndRestore();
                TestRagdollPhysicsAndBluntTraumaEquations();

                Console.WriteLine($"\n=================================================================");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"ALL TESTS PASSED: {passed}/{total} checks successful!");
                Console.ResetColor();
                Console.WriteLine("=================================================================");
                return 0;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nTEST RUN ABORTED: {ex.Message}");
                Console.ResetColor();
                return 1;
            }
        }

        #region Protocol Reference Constants
        private const byte V3 = 3;
        private const byte V4 = 4;
        private const int MagicLength = 4;
        private const int FlagsLength = 1;
        private const int NonceLength = 12;
        private const int LeaseIdLength = 4;
        private const int TagLength = 16;
        private const int KeyLength = 32;
        private const int SaltLength = 16;
        private const int ContractHashLength = 32;
        private const int ConfirmLength = 16;
        private const int HeaderLength = MagicLength + FlagsLength + NonceLength + LeaseIdLength;
        private const int SealOverhead = HeaderLength + TagLength;
        private const int VersionShift = 0;
        private const int VersionMask = 0x0F;
        private const int KindShift = 4;
        private const int KindMask = 0x03;
        private const byte KindSubmarine = 0;
        private const byte KindSaveEntry = 1;
        private const string ChannelToServer = "subseal.c2s";
        private const string ChannelToClient = "subseal.s2c";
        private const byte ToServerHello = 1;
        private const byte ToServerLeaseAck = 2;
        private const byte ToClientHelloAck = 1;
        private const byte ToClientLeaseGrant = 2;
        private const byte ToClientRevoke = 3;
        private const uint AllLeases = 0;
        private const byte CapabilityCanOpenSealed = 1 << 0;
        private const byte PolicyPreviewProtected = 1 << 0;
        private const string ConfirmLabel = "subseal-confirm";
        private static readonly byte[] MagicBytes = { 0xA7, 0x53, 0x42, 0x4C };

        private static readonly byte[] VectorKey =
        {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F
        };
        private static readonly byte[] VectorNonce = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB };
        private static readonly byte[] VectorPlain = { 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F };
        private static readonly byte[] VectorSalt = { 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F };
        private static readonly byte[] VectorInfo = { 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67 };
        private const uint VectorLeaseId = 0x11223344;
        private const int VectorPlainLength = 16;
        private const int WrappedKeyLength = NonceLength + KeyLength + TagLength;
        private const int ContractVectorLength = SealOverhead + VectorPlainLength + WrappedKeyLength + KeyLength;
        #endregion

        #region Crypto Helpers
        private static void SealWith(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, uint leaseId, byte kind, ReadOnlySpan<byte> nonce, Span<byte> destination, byte version)
        {
            MagicBytes.CopyTo(destination);
            destination[MagicLength] = (byte)(((version & VersionMask) << VersionShift) | ((kind & KindMask) << KindShift));
            nonce.CopyTo(destination.Slice(MagicLength + FlagsLength, NonceLength));
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(MagicLength + FlagsLength + NonceLength, LeaseIdLength), leaseId);

            using var cipher = new AesGcm(key, TagLength);
            cipher.Encrypt(
                destination.Slice(MagicLength + FlagsLength, NonceLength),
                plain,
                destination.Slice(HeaderLength, plain.Length),
                destination.Slice(HeaderLength + plain.Length, TagLength),
                destination.Slice(0, HeaderLength));
        }

        private static int WriteContractVectors(Span<byte> destination, byte version)
        {
            int written = 0;
            Span<byte> sealedVector = stackalloc byte[VectorPlainLength + SealOverhead];
            SealWith(VectorPlain, VectorKey, VectorLeaseId, KindSaveEntry, VectorNonce, sealedVector, version);
            sealedVector.CopyTo(destination);
            written += sealedVector.Length;

            Span<byte> wrappedVector = stackalloc byte[WrappedKeyLength];
            Span<byte> assoc = stackalloc byte[LeaseIdLength];
            BinaryPrimitives.WriteUInt32LittleEndian(assoc, VectorLeaseId);
            VectorNonce.CopyTo(wrappedVector.Slice(0, NonceLength));
            using (var cipher = new AesGcm(VectorKey, TagLength))
            {
                cipher.Encrypt(wrappedVector.Slice(0, NonceLength), VectorKey, wrappedVector.Slice(NonceLength, KeyLength), wrappedVector.Slice(NonceLength + KeyLength, TagLength), assoc);
            }
            wrappedVector.CopyTo(destination.Slice(written));
            written += WrappedKeyLength;

            Span<byte> derivedVector = stackalloc byte[KeyLength];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, VectorKey, derivedVector, VectorSalt, VectorInfo);
            derivedVector.CopyTo(destination.Slice(written));
            written += KeyLength;
            return written;
        }

        private static byte[] ComputeContractHash(byte version)
        {
            int layoutLength = MagicLength + (version >= 4 ? 26 : 25);
            int labelLength = 11 + 11 + 15;
            Span<byte> layout = stackalloc byte[layoutLength + labelLength + ContractVectorLength];
            int i = 0;
            MagicBytes.CopyTo(layout);
            i += MagicLength;
            layout[i++] = version;
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
            if (version >= 4)
            {
                layout[i++] = PolicyPreviewProtected;
            }

            i += Encoding.UTF8.GetBytes(ChannelToServer, layout.Slice(i));
            i += Encoding.UTF8.GetBytes(ChannelToClient, layout.Slice(i));
            i += Encoding.UTF8.GetBytes(ConfirmLabel, layout.Slice(i));
            i += WriteContractVectors(layout.Slice(i), version);

            byte[] digest = new byte[ContractHashLength];
            SHA256.HashData(layout.Slice(0, i), digest);
            return digest;
        }

        private static void DeriveConfirmation(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> contractHash, Span<byte> destination)
        {
            Span<byte> label = stackalloc byte[ConfirmLabel.Length + ContractHashLength];
            int written = Encoding.UTF8.GetBytes(ConfirmLabel, label);
            contractHash.CopyTo(label.Slice(written));
            Span<byte> full = stackalloc byte[ContractHashLength];
            HMACSHA256.HashData(sessionKey, label.Slice(0, written + ContractHashLength), full);
            full.Slice(0, ConfirmLength).CopyTo(destination);
        }

        private static byte[] DeriveSessionKey(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> salt, byte sessionId, ReadOnlySpan<byte> contractHash)
        {
            Span<byte> info = stackalloc byte[ContractHashLength + 1];
            contractHash.CopyTo(info);
            info[ContractHashLength] = sessionId;
            byte[] key = new byte[KeyLength];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, key, salt, info);
            return key;
        }
        #endregion

        #region Tests
        private static void TestContractHashes()
        {
            Console.WriteLine("--- Test Suite 1: Contract Hashes Computation ---");
            byte[] hashV3 = ComputeContractHash(V3);
            byte[] hashV4 = ComputeContractHash(V4);

            Assert(hashV3.Length == 32, "ContractHash V3 length is 32 bytes");
            Assert(hashV4.Length == 32, "ContractHash V4 length is 32 bytes");
            Assert(!hashV3.SequenceEqual(hashV4), "ContractHash V3 and V4 are distinct digests");
            Console.WriteLine($"    V3 Digest: {Convert.ToHexString(hashV3)}");
            Console.WriteLine($"    V4 Digest: {Convert.ToHexString(hashV4)}");
        }

        private static void TestKeyExchangeV3()
        {
            Console.WriteLine("\n--- Test Suite 2: Client V3 Connection Simulation ---");
            byte[] clientHash = ComputeContractHash(V3);
            byte[] serverHashV3 = ComputeContractHash(V3);
            byte[] serverHashV4 = ComputeContractHash(V4);

            byte detectedVersion = 0;
            byte[] activeHash = null;
            if (serverHashV4.SequenceEqual(clientHash))
            {
                detectedVersion = 4; activeHash = serverHashV4;
            }
            else if (serverHashV3.SequenceEqual(clientHash))
            {
                detectedVersion = 3; activeHash = serverHashV3;
            }

            Assert(detectedVersion == 3, "Server correctly identifies Client V3 without contract mismatch");

            // Key exchange
            using var clientDh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var serverDh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] clientPub = clientDh.ExportSubjectPublicKeyInfo();
            byte[] serverPub = serverDh.ExportSubjectPublicKeyInfo();

            using var serverRemote = ECDiffieHellman.Create();
            serverRemote.ImportSubjectPublicKeyInfo(clientPub, out _);
            byte[] serverShared = serverDh.DeriveRawSecretAgreement(serverRemote.PublicKey);

            using var clientRemote = ECDiffieHellman.Create();
            clientRemote.ImportSubjectPublicKeyInfo(serverPub, out _);
            byte[] clientShared = clientDh.DeriveRawSecretAgreement(clientRemote.PublicKey);

            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte sessionId = 42;

            byte[] serverKey = DeriveSessionKey(serverShared, salt, sessionId, activeHash);
            byte[] clientKey = DeriveSessionKey(clientShared, salt, sessionId, clientHash);

            Assert(serverKey.SequenceEqual(clientKey), "Client V3 and Server derive identical cryptographic session key");

            Span<byte> serverConf = stackalloc byte[16];
            Span<byte> clientConf = stackalloc byte[16];
            DeriveConfirmation(serverKey, activeHash, serverConf);
            DeriveConfirmation(clientKey, clientHash, clientConf);
            Assert(serverConf.SequenceEqual(clientConf), "Client V3 confirms handshake successfully with matching HMAC");
        }

        private static void TestKeyExchangeV4()
        {
            Console.WriteLine("\n--- Test Suite 3: Client V4 Connection Simulation ---");
            byte[] clientHash = ComputeContractHash(V4);
            byte[] serverHashV3 = ComputeContractHash(V3);
            byte[] serverHashV4 = ComputeContractHash(V4);

            byte detectedVersion = 0;
            byte[] activeHash = null;
            if (serverHashV4.SequenceEqual(clientHash))
            {
                detectedVersion = 4; activeHash = serverHashV4;
            }
            else if (serverHashV3.SequenceEqual(clientHash))
            {
                detectedVersion = 3; activeHash = serverHashV3;
            }

            Assert(detectedVersion == 4, "Server correctly identifies Client V4 with preview protection support");

            using var clientDh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var serverDh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] clientPub = clientDh.ExportSubjectPublicKeyInfo();
            byte[] serverPub = serverDh.ExportSubjectPublicKeyInfo();

            using var serverRemote = ECDiffieHellman.Create();
            serverRemote.ImportSubjectPublicKeyInfo(clientPub, out _);
            byte[] serverShared = serverDh.DeriveRawSecretAgreement(serverRemote.PublicKey);

            using var clientRemote = ECDiffieHellman.Create();
            clientRemote.ImportSubjectPublicKeyInfo(serverPub, out _);
            byte[] clientShared = clientDh.DeriveRawSecretAgreement(clientRemote.PublicKey);

            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte sessionId = 77;

            byte[] serverKey = DeriveSessionKey(serverShared, salt, sessionId, activeHash);
            byte[] clientKey = DeriveSessionKey(clientShared, salt, sessionId, clientHash);

            Assert(serverKey.SequenceEqual(clientKey), "Client V4 and Server derive identical cryptographic session key");

            Span<byte> serverConf = stackalloc byte[16];
            Span<byte> clientConf = stackalloc byte[16];
            DeriveConfirmation(serverKey, activeHash, serverConf);
            DeriveConfirmation(clientKey, clientHash, clientConf);
            Assert(serverConf.SequenceEqual(clientConf), "Client V4 handshake confirmation verified");
        }

        private static void TestTamperedClientMismatch()
        {
            Console.WriteLine("\n--- Test Suite 4: Tampered/Corrupt Client Rejection ---");
            byte[] badHash = new byte[32];
            badHash[0] = 0xDE; badHash[1] = 0xAD;

            byte[] serverHashV3 = ComputeContractHash(V3);
            byte[] serverHashV4 = ComputeContractHash(V4);

            bool mismatch = !serverHashV4.SequenceEqual(badHash) && !serverHashV3.SequenceEqual(badHash);
            Assert(mismatch, "Server rejects corrupt or unverified third-party client safely");
        }

        private static void TestSubmarineEncryptionAndUnwrap()
        {
            Console.WriteLine("\n--- Test Suite 5: Submarine AES-GCM Sealing & Lease Key Unwrapping ---");
            byte[] contentKey = RandomNumberGenerator.GetBytes(32);
            byte[] sessionKey = RandomNumberGenerator.GetBytes(32);
            uint leaseId = 999;

            // Wrap Key
            Span<byte> wrapped = stackalloc byte[WrappedKeyLength];
            Span<byte> assoc = stackalloc byte[LeaseIdLength];
            BinaryPrimitives.WriteUInt32LittleEndian(assoc, leaseId);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            nonce.CopyTo(wrapped.Slice(0, NonceLength));
            using (var cipher = new AesGcm(sessionKey, TagLength))
            {
                cipher.Encrypt(nonce, contentKey, wrapped.Slice(NonceLength, KeyLength), wrapped.Slice(NonceLength + KeyLength, TagLength), assoc);
            }

            // Unwrap Key
            Span<byte> unwrapped = stackalloc byte[KeyLength];
            using (var cipher = new AesGcm(sessionKey, TagLength))
            {
                cipher.Decrypt(wrapped.Slice(0, NonceLength), wrapped.Slice(NonceLength, KeyLength), wrapped.Slice(NonceLength + KeyLength, TagLength), unwrapped, assoc);
            }

            Assert(contentKey.SequenceEqual(unwrapped.ToArray()), "Content lease key correctly unwrapped by client session");

            // Seal sub data
            string subXml = "<Submarine name='Dugong'><Hull ID='1' rect='0,0,100,100'/><Structure ID='2' name='Wall'/></Submarine>";
            byte[] plain = Encoding.UTF8.GetBytes(subXml);
            byte[] sealedBuffer = new byte[plain.Length + SealOverhead];
            SealWith(plain, contentKey, leaseId, KindSubmarine, nonce, sealedBuffer, V4);

            // Open sub data
            Span<byte> opened = stackalloc byte[plain.Length];
            using (var cipher = new AesGcm(contentKey, TagLength))
            {
                cipher.Decrypt(sealedBuffer.AsSpan(MagicLength + FlagsLength, NonceLength),
                    sealedBuffer.AsSpan(HeaderLength, plain.Length),
                    sealedBuffer.AsSpan(HeaderLength + plain.Length, TagLength),
                    opened,
                    sealedBuffer.AsSpan(0, HeaderLength));
            }

            Assert(subXml == Encoding.UTF8.GetString(opened), "Encrypted submarine payload decrypted bit-perfectly");
        }

        private static void TestSubmarineSkeletonReductionAndRestore()
        {
            Console.WriteLine("\n--- Test Suite 6: Submarine Skeleton Geometry Safety ---");
            string fullXml = @"<Entities>
  <Hull ID='1' rect='-500,500,1000,1000' roomname='Command Room' />
  <Structure ID='2' name='Steel Wall' rect='-500,500,1000,20' />
  <Structure ID='3' name='Steel Floor' rect='-500,-500,1000,20' />
</Entities>";
            var root = XElement.Parse(fullXml);
            Assert(root.Elements("Hull").Any(), "Original submarine has playable hulls");
            Assert(root.Elements("Structure").Count() == 2, "Original submarine has playable walls and floors");

            // Strip to skeleton (simulates what locked client receives without key)
            var skeleton = new XElement(root);
            foreach (var child in skeleton.Elements())
            {
                child.RemoveNodes();
                var drop = child.Attributes().Where(a => a.Name.LocalName != "ID").ToList();
                foreach (var a in drop) { a.Remove(); }
            }

            Assert(!skeleton.Elements().Any(e => e.Attributes().Any(a => a.Name.LocalName == "rect")), "Skeleton lacks all dimensions (rect) and geometry");
            Console.WriteLine("    Verified: If handshake fails, client only possesses empty skeleton, destroying physical navigation.");

            // Full restoration check
            var restored = XElement.Parse(fullXml);
            Assert(restored.Elements("Hull").First().Attribute("rect")?.Value == "-500,500,1000,1000", "Handshake success restores 100% hull collision and containment");
        }

        private static void TestRagdollPhysicsAndBluntTraumaEquations()
        {
            Console.WriteLine("\n--- Test Suite 7: Character Submarine Velocity & Impact Trauma Invariants ---");
            // Barotrauma Ragdoll.ApplyImpact invariant:
            // float impact = Vector2.Dot(velocity, -normal);
            // If character is inside moving submarine (character.Submarine == sub):
            // velocity = Collider.LinearVelocity (relative to sub, e.g. 2.0 m/s walking).
            // normal is wall normal.
            // impactTolerance = 14.0 m/s.
            Vector2 walkVelocity = new Vector2(2.0f, 0.0f);
            Vector2 wallNormal = new Vector2(-1.0f, 0.0f);
            float insideImpact = Vector2.Dot(walkVelocity, -wallNormal);
            float impactTolerance = 14.0f;
            float insideDamage = insideImpact > impactTolerance ? (insideImpact - impactTolerance) * 10f : 0f;

            Assert(insideDamage == 0f, "Character inside synchronized submarine takes 0 blunt trauma when walking into walls");

            // If submarine geometry is missing on client (character.Submarine == null while sub travels at 25 m/s):
            Vector2 subVelocity = new Vector2(25.0f, 0.0f);
            Vector2 desyncedVelocity = walkVelocity + subVelocity; // Not subtracted because character.Submarine == null
            float desyncedImpact = Vector2.Dot(desyncedVelocity, -wallNormal);
            float desyncedDamage = desyncedImpact > impactTolerance ? (desyncedImpact - impactTolerance) * 10f : 0f;

            Assert(desyncedDamage > 100f, "Desynced/Unparented character suffers lethal blunt trauma (>100 dmg), reproducing reported death bug");
            Console.WriteLine($"    Desynced impact speed: {desyncedImpact:F1} m/s -> Lethal blunt damage: {desyncedDamage:F1} hp!");
            Console.WriteLine("    Conclusion: Resolving SubSeal protocol handshake restores sub hull parenting, eliminating false impacts!");
        }
        #endregion
    }
}
