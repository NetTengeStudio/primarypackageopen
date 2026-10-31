using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using SubSeal.Wire;

namespace SubSeal.Client
{
    internal static class OpenPatches
    {
        private static LeaseStore leases;

        internal static void Bind(LeaseStore store) => leases = store;

        public static bool BeforeDecompressFileToStream(string fileName, ref Stream __result)
        {
            if (!TryOpenSealed(fileName, out Stream opened)) { return true; }
            __result = opened;
            return false;
        }

        private static bool TryOpenSealed(string fileName, out Stream opened)
        {
            opened = null;
            if (!SealedFile.TryRead(fileName, out byte[] content)) { return false; }

            Artifacts.Track(fileName);
            opened = Unseal(content);
            return opened is not null;
        }

        private static Stream Unseal(byte[] content)
        {
            uint leaseId = TransferSeal.ReadLeaseId(content);
            if (leases is null || !leases.TryGet(leaseId, out byte[] contentKey)) { return null; }

            int plainLength = TransferSeal.PlainLength(content.Length);
            byte[] plain = ArrayPool<byte>.Shared.Rent(plainLength);

            try
            {
                if (!TransferSeal.TryOpen(content, contentKey, plain, out int written)) { return null; }

                using MemoryStream compressed = new MemoryStream(plain, 0, written, false);
                using GZipStream decompressor = new GZipStream(compressed, CompressionMode.Decompress);
                MemoryStream result = new MemoryStream(written * 2);
                decompressor.CopyTo(result);
                result.Position = 0;
                return result;
            }
            catch (InvalidDataException)
            {
                return null;
            }
            finally
            {
                Array.Clear(plain, 0, plainLength);
                ArrayPool<byte>.Shared.Return(plain);
            }
        }
    }
}
