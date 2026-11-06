using System;
using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.SubEditor;

public static class WorldMutationCoordinator
{
    [ThreadStatic] private static int applying;
    [ThreadStatic] private static int rollingBack;

    public static bool Applying => applying != 0;
    public static bool RollingBack => rollingBack != 0;

    public static bool Apply(OpKind kind, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload, out BatchPayload.Sub inverse)
    {
        inverse = default;
        using var world = WorldContext.Enter();
        if (!FrameApplier.CaptureInverse(kind, ids, payload, out inverse)) { return false; }

        bool changed = false;
        bool restored = false;
        applying++;
        try
        {
            using (LiveGeometry.Enter())
            {
                changed = true;
                if (!FrameApplier.Apply(kind, ids, payload))
                {
                    Restore(inverse);
                    restored = true;
                    return false;
                }
            }
            return true;
        }
        catch
        {
            if (changed && !restored)
            {
                try { RestoreAfterFailedCommit(inverse); }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException("SubEditor world mutation failed and its rollback could not restore the world", rollbackError);
                }
            }
            throw;
        }
        finally { applying--; }
    }

    public static bool ApplyBatch(ReadOnlySpan<byte> payload)
    {
        if (!BatchPayload.TryRead(payload, SubEditorProtocol.MaxFrameBytes, out BatchPayload.Sub[] operations)) { return false; }
        var undo = new List<BatchPayload.Sub>(operations.Length);
        using var world = WorldContext.Enter();
        applying++;
        bool restored = false;
        try
        {
            using (LiveGeometry.Enter())
            {
                foreach (BatchPayload.Sub operation in operations)
                {
                    if (!FrameApplier.CaptureInverse(operation.Kind, operation.Ids, operation.Payload, out BatchPayload.Sub inverse))
                    {
                        RestoreReverse(undo);
                        restored = true;
                        return false;
                    }
                    undo.Add(inverse);
                    if (!FrameApplier.Apply(operation.Kind, operation.Ids, operation.Payload))
                    {
                        RestoreReverse(undo);
                        restored = true;
                        return false;
                    }
                }
            }
            return true;
        }
        catch
        {
            if (!restored)
            {
                try
                {
                    using (LiveGeometry.Enter()) { RestoreReverse(undo); }
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException("SubEditor batch commit and rollback both failed", rollbackError);
                }
            }
            throw;
        }
        finally { applying--; }
    }

    private static void RestoreAfterFailedCommit(BatchPayload.Sub inverse)
    {
        using (LiveGeometry.Enter()) { Restore(inverse); }
    }

    private static void RestoreReverse(List<BatchPayload.Sub> undo)
    {
        for (int i = undo.Count - 1; i >= 0; i--) { Restore(undo[i]); }
    }

    private static void Restore(BatchPayload.Sub inverse)
    {
        rollingBack++;
        try { FrameApplier.Restore(inverse); }
        finally { rollingBack--; }
    }
}
