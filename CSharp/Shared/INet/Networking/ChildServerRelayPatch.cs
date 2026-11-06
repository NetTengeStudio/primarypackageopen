#nullable enable
using System;
using System.Reflection;
using System.Threading;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace INet;

public sealed class ChildServerRelayPatchPlugin : PrimaryPackage.Core.IModule
{
    public void RoundStart() { }

    public void Attach(Harmony harmony)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(ChildServerRelay), "CheckPipeConnected");
            if (target != null)
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ChildServerRelayPatch), nameof(ChildServerRelayPatch.Prefix))));
            }
        }
        catch (Exception e)
        {
            DebugConsole.ThrowError("Failed to patch ChildServerRelay.CheckPipeConnected", e);
        }
    }

    public void Release() { }
}

internal static class ChildServerRelayPatch
{
    private static readonly FieldInfo? StatusField = AccessTools.Field(typeof(ChildServerRelay), "status");
    private static readonly FieldInfo? WriteEventField = AccessTools.Field(typeof(ChildServerRelay), "writeManualResetEvent");
    private static readonly FieldInfo? CancelTokenField = AccessTools.Field(typeof(ChildServerRelay), "readCancellationToken");

    public static bool Prefix(string name, object pipe)
    {
        if (pipe == null) { return true; }
        bool isConnected = false;
        try
        {
            PropertyInfo? isConnectedProp = pipe.GetType().GetProperty("IsConnected");
            if (isConnectedProp != null)
            {
                isConnected = (bool)(isConnectedProp.GetValue(pipe) ?? false);
            }
        }
        catch
        {
            isConnected = false;
        }

        if (!isConnected)
        {
            MarkShutDown();
            return false;
        }

        return true;
    }

    public static Exception? ThreadFinalizer(Exception __exception, System.Reflection.MethodBase __originalMethod)
    {
        if (__exception == null) { return null; }
        Exception? outcome = PrimaryPackage.Fault.FaultHooks.ContainBoundary(__originalMethod, __exception);
        if (outcome == null) { MarkShutDown(); }
        return outcome;
    }

    private static void MarkShutDown()
    {
        if (StatusField != null)
        {
            Type? statusType = StatusField.FieldType;
            if (statusType != null && Enum.TryParse(statusType, "ShutDown", out object? shutDownVal))
            {
                StatusField.SetValue(null, shutDownVal);
            }
        }

        if (WriteEventField?.GetValue(null) is ManualResetEvent resetEvent)
        {
            try { resetEvent.Set(); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("ChildServerRelayPatch.1", noted); }
        }

        if (CancelTokenField?.GetValue(null) is CancellationTokenSource cts)
        {
            try { cts.Cancel(); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("ChildServerRelayPatch.2", noted); }
        }
    }
}
