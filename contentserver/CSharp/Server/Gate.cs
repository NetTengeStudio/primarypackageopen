using System;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace SubSeal.Server
{
    internal enum GateMode : byte
    {
        None,

        Sealing,

        RefusingAllTransfers,

        RefusingRequestsOnly,

        Open
    }

    internal static class Gate
    {
        internal static GateMode Mode { get; private set; } = GateMode.None;

        internal static bool CoversServerInitiatedTransfers => Covers(Mode);

        internal static GateMode Choose(bool canSeal, bool canRefuseAtTransfer, bool canRefuseAtRequest, bool allowPlaintext)
        {
            if (canSeal) { return GateMode.Sealing; }
            if (canRefuseAtTransfer) { return GateMode.RefusingAllTransfers; }
            if (canRefuseAtRequest) { return GateMode.RefusingRequestsOnly; }
            return allowPlaintext ? GateMode.Open : GateMode.None;
        }

        internal static bool Covers(GateMode mode)
            => mode == GateMode.Sealing || mode == GateMode.RefusingAllTransfers;

        internal static GateMode Install(Harmony harmony, Settings settings)
        {
            EngineSymbols.Resolve();

            foreach (string symbol in EngineSymbols.Unresolved)
            {
                Diagnostics.Fault($"{symbol} not found");
            }

            bool canSeal = EngineSymbols.CanSeal;
            bool canRefuseAtTransfer = EngineSymbols.CanRefuseAtTransfer;
            bool canRefuseAtRequest = EngineSymbols.CanRefuseAtRequest;

            while (true)
            {
                GateMode chosen = Choose(canSeal, canRefuseAtTransfer, canRefuseAtRequest, settings.AllowPlaintextWhenUnenforceable);

                if (chosen == GateMode.Sealing)
                {
                    if (TryPatchSealing(harmony)) { Mode = chosen; return Mode; }
                    canSeal = false;
                    continue;
                }

                if (chosen == GateMode.RefusingAllTransfers)
                {
                    if (TryPatchRefusal(harmony))
                    {
                        Diagnostics.Fault("sealing unavailable, all submarine transfers refused");
                        Mode = chosen;
                        return Mode;
                    }
                    canRefuseAtTransfer = false;
                    continue;
                }

                if (chosen == GateMode.RefusingRequestsOnly)
                {
                    if (TryPatchRequestRefusal(harmony))
                    {
                        Diagnostics.Fault("CRITICAL: sealing and transfer-start refusal unavailable; requests refused, but unsolicited campaign saves may leak protected data");
                        Mode = chosen;
                        return Mode;
                    }
                    canRefuseAtRequest = false;
                    continue;
                }

                Diagnostics.Fault("no enforcement point, every submarine is served unsealed regardless of settings");
                Mode = chosen;
                return Mode;
            }
        }

        internal static void Forget() => Mode = GateMode.None;

        private static bool TryPatchSealing(Harmony harmony)
        {
            try
            {
                harmony.Patch(
                    EngineSymbols.StartTransfer,
                    new HarmonyMethod(typeof(TransferPatches).GetMethod(nameof(TransferPatches.BeforeStartTransfer))),
                    new HarmonyMethod(typeof(TransferPatches).GetMethod(nameof(TransferPatches.AfterStartTransfer))));
                return true;
            }
            catch (Exception)
            {
                Diagnostics.Fault("FileSender.StartTransfer patch rejected");
                Rollback(harmony, EngineSymbols.StartTransfer);
                return false;
            }
        }

        private static bool TryPatchRefusal(Harmony harmony)
        {
            try
            {
                harmony.Patch(
                    EngineSymbols.StartTransfer,
                    new HarmonyMethod(typeof(BlockPatches).GetMethod(nameof(BlockPatches.BeforeStartTransfer))));
                return true;
            }
            catch (Exception)
            {
                Diagnostics.Fault("FileSender.StartTransfer refusal patch rejected");
                Rollback(harmony, EngineSymbols.StartTransfer);
                return false;
            }
        }

        private static bool TryPatchRequestRefusal(Harmony harmony)
        {
            try
            {
                harmony.Patch(
                    EngineSymbols.ReadFileRequest,
                    new HarmonyMethod(typeof(BlockPatches).GetMethod(nameof(BlockPatches.BeforeReadFileRequest))));
                return true;
            }
            catch (Exception)
            {
                Diagnostics.Fault("FileSender.ReadFileRequest patch rejected");
                Rollback(harmony, EngineSymbols.ReadFileRequest);
                return false;
            }
        }

        private static void Rollback(Harmony harmony, System.Reflection.MethodBase target)
        {
            if (target is null) { return; }

            try
            {
                harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id);
            }
            catch (Exception)
            {
                Diagnostics.Fault($"{target.Name} rollback rejected");
            }
        }
    }

    internal static class BlockPatches
    {
        public static bool BeforeStartTransfer(NetworkConnection recipient, FileTransferType fileType, string filePath)
        {
            if (recipient is null) { return false; }
            if (recipient == GameMain.Server?.OwnerConnection) { return true; }
            if (!ModTransferPolicy.Allows(fileType, filePath))
            {
                Diagnostics.Note($"{Diagnostics.Describe(recipient)} refused mod archive");
                return false;
            }
            if (fileType != FileTransferType.Submarine && fileType != FileTransferType.CampaignSave) { return true; }

            Diagnostics.Note($"{Diagnostics.Describe(recipient)} refused");
            return false;
        }

        public static bool BeforeReadFileRequest(IReadMessage inc)
        {
            if (inc is null) { return true; }
            if (inc.Sender is not null && inc.Sender == GameMain.Server?.OwnerConnection) { return true; }

            int mark = inc.BitPosition;

            FileTransferMessageType messageType = (FileTransferMessageType)inc.ReadByte();
            if (messageType != FileTransferMessageType.Initiate)
            {
                inc.BitPosition = mark;
                return true;
            }

            FileTransferType fileType = (FileTransferType)inc.ReadByte();
            inc.BitPosition = mark;

            // The request-only fallback cannot identify a mod archive without decoding
            // the remainder of the request. Refuse all mod requests in protected mode.
            if (fileType == FileTransferType.Mod && ModTransferPolicy.RefuseAllModRequestsInRequestOnlyMode)
            {
                Diagnostics.Note($"{Diagnostics.Describe(inc.Sender)} refused mod request");
                return false;
            }
            if (fileType != FileTransferType.Submarine && fileType != FileTransferType.CampaignSave) { return true; }

            Diagnostics.Note($"{Diagnostics.Describe(inc.Sender)} refused");
            return false;
        }
    }
}
