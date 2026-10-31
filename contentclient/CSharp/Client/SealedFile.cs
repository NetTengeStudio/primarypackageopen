using System;
using System.IO;
using SubSeal.Wire;

namespace SubSeal.Client
{
    internal enum SealProbe : byte
    {
        Absent,
        Present,
        Unreadable
    }

    internal static class SealedFile
    {
        internal static SealProbe Probe(string path)
        {
            if (string.IsNullOrEmpty(path)) { return SealProbe.Absent; }
            if (!File.Exists(path)) { return SealProbe.Absent; }

            try
            {
                using FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length < Protocol.SealOverhead) { return SealProbe.Absent; }

                Span<byte> header = stackalloc byte[Protocol.HeaderLength];
                if (file.Read(header) != Protocol.HeaderLength) { return SealProbe.Absent; }
                return TransferSeal.CarriesHeader(header) ? SealProbe.Present : SealProbe.Absent;
            }
            catch (IOException)
            {
                return SealProbe.Unreadable;
            }
            catch (UnauthorizedAccessException)
            {
                return SealProbe.Unreadable;
            }
        }

        internal static bool CarriesSeal(string path) => Probe(path) == SealProbe.Present;

        internal static bool TryReadLeaseId(string path, out uint leaseId)
        {
            leaseId = 0;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return false; }

            try
            {
                using FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length < Protocol.SealOverhead) { return false; }

                Span<byte> header = stackalloc byte[Protocol.HeaderLength];
                if (file.Read(header) != Protocol.HeaderLength) { return false; }
                if (!TransferSeal.CarriesHeader(header)) { return false; }

                leaseId = TransferSeal.ReadLeaseId(header);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static bool TryRead(string path, out byte[] content)
        {
            content = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return false; }

            try
            {
                using FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length < Protocol.SealOverhead) { return false; }

                Span<byte> header = stackalloc byte[Protocol.HeaderLength];
                if (file.Read(header) != Protocol.HeaderLength) { return false; }
                if (!TransferSeal.CarriesHeader(header)) { return false; }

                byte[] buffer = new byte[file.Length];
                header.CopyTo(buffer);
                file.ReadExactly(buffer, Protocol.HeaderLength, buffer.Length - Protocol.HeaderLength);
                content = buffer;
                return true;
            }
            catch (IOException)
            {
                content = null;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                content = null;
                return false;
            }
        }
    }
}
