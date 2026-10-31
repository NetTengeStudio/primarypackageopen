using System.Collections.Generic;
using System.Reflection;
using Barotrauma.Networking;

namespace SubSeal.Server
{
    
    internal static class EngineSymbols
    {
        private static readonly List<string> unresolved = new List<string>();

        internal static MethodInfo StartTransfer { get; private set; }

        internal static FieldInfo TransferData { get; private set; }

        internal static MethodInfo ReadFileRequest { get; private set; }

        internal static IReadOnlyList<string> Unresolved => unresolved;

        internal static bool CanSeal => StartTransfer is not null && TransferData is not null;

        internal static bool CanRefuseAtTransfer => StartTransfer is not null;

        internal static bool CanRefuseAtRequest => ReadFileRequest is not null;

        internal static void Resolve()
        {
            unresolved.Clear();

            StartTransfer = typeof(FileSender).GetMethod(
                nameof(FileSender.StartTransfer),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(NetworkConnection), typeof(FileTransferType), typeof(string) },
                null);

            if (StartTransfer is null) { unresolved.Add("FileSender.StartTransfer"); }

            TransferData = typeof(FileSender.FileTransferOut).GetField(
                $"<{nameof(FileSender.FileTransferOut.Data)}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (TransferData is null || TransferData.FieldType != typeof(byte[]))
            {
                TransferData = null;
                unresolved.Add("FileSender.FileTransferOut.Data");
            }

            ReadFileRequest = typeof(FileSender).GetMethod(
                nameof(FileSender.ReadFileRequest),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(IReadMessage), typeof(Client) },
                null);

            if (ReadFileRequest is null) { unresolved.Add("FileSender.ReadFileRequest"); }
        }

        internal static void Forget()
        {
            StartTransfer = null;
            TransferData = null;
            ReadFileRequest = null;
            unresolved.Clear();
        }

        internal static void OverwriteData(FileSender.FileTransferOut transfer, byte[] replacement)
            => TransferData.SetValue(transfer, replacement);
    }
}
