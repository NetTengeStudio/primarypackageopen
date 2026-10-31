using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using SubSeal.Wire;

namespace SubSeal.Server
{
    internal static class SaveResealer
    {
        private const int MaxNameCharacters = 255;

        internal static byte[] Reseal(byte[] container, ReadOnlySpan<byte> key, uint leaseId)
        {
            try
            {
                using MemoryStream plain = Inflate(container);
                using MemoryStream rebuilt = new MemoryStream(plain.Capacity);

                using (BinaryReader reader = new BinaryReader(plain, Encoding.Unicode, true))
                using (BinaryWriter writer = new BinaryWriter(rebuilt, Encoding.Unicode, true))
                {
                    while (plain.Position < plain.Length)
                    {
                        int nameLength = reader.ReadInt32();
                        if (nameLength < 0 || nameLength > MaxNameCharacters) { return null; }

                        byte[] nameBytes = reader.ReadBytes(nameLength * sizeof(char));
                        if (nameBytes.Length != nameLength * sizeof(char)) { return null; }

                        int contentLength = reader.ReadInt32();
                        if (contentLength < 0) { return null; }

                        byte[] content = reader.ReadBytes(contentLength);
                        if (content.Length != contentLength) { return null; }

                        string name = Encoding.Unicode.GetString(nameBytes);
                        bool isSubmarine = name.EndsWith(".sub", StringComparison.OrdinalIgnoreCase);

                        writer.Write(nameLength);
                        writer.Write(nameBytes);

                        if (isSubmarine)
                        {
                            byte[] sealedContent = new byte[TransferSeal.SealedLength(content.Length)];
                            TransferSeal.Seal(content, key, leaseId, Protocol.KindSaveEntry, sealedContent);
                            writer.Write(sealedContent.Length);
                            writer.Write(sealedContent);
                        }
                        else
                        {
                            writer.Write(contentLength);
                            writer.Write(content);
                        }
                    }
                }

                return Deflate(rebuilt);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MemoryStream Inflate(byte[] container)
        {
            using MemoryStream source = new MemoryStream(container, false);
            using GZipStream decompressor = new GZipStream(source, CompressionMode.Decompress);
            MemoryStream plain = new MemoryStream(container.Length * 2);
            decompressor.CopyTo(plain);
            plain.Position = 0;
            return plain;
        }

        private static byte[] Deflate(MemoryStream rebuilt)
        {
            rebuilt.Position = 0;
            using MemoryStream target = new MemoryStream((int)rebuilt.Length);
            using (GZipStream compressor = new GZipStream(target, CompressionMode.Compress, true))
            {
                rebuilt.CopyTo(compressor);
            }
            return target.ToArray();
        }
    }
}
