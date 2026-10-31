using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public static class EventFrame {
    private static IReadMessage message;
    private static int start;
    private static int end = -1;

    public static void Open(uint length, IReadMessage msg) {
        message = msg;
        start = msg.BitPosition;
        end = start + (int)length * 8;
    }

    public static bool Covers(IReadMessage msg) =>
        ReferenceEquals(msg, message) && msg.BitPosition == start && end >= start && end <= msg.LengthBits;

    public static void Skip(IReadMessage msg, bool framed) {
        if (!framed) { throw new InvalidOperationException("entity event has no frame to skip"); }
        msg.BitPosition = end;
    }

    public static bool SkipToEnd(IReadMessage msg) {
        if (!ReferenceEquals(msg, message) || end < msg.BitPosition || end > msg.LengthBits) { return false; }
        msg.BitPosition = end;
        return true;
    }

    public static void Resync(IReadMessage msg) {
        if (!ReferenceEquals(msg, message) || end < start || end > msg.LengthBits || msg.BitPosition == end) { return; }
        ModLog.Once("entity event was read to a different length than the server wrote it, resynced to the frame end");
        msg.BitPosition = end;
    }

    public static void Clear() {
        message = null;
        end = -1;
    }

    public static IEnumerable<CodeInstruction> Mark(IEnumerable<CodeInstruction> instructions, MethodBase original) {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo open = AccessTools.Method(typeof(EventFrame), nameof(Open));
        for (int i = 0; i < code.Count; i++) {
            if (code[i].opcode == OpCodes.Call && code[i].operand is MethodInfo existing &&
                existing.Name == nameof(Open) && existing.DeclaringType?.FullName == typeof(EventFrame).FullName) {
                return code;
            }
        }
        MethodInfo resync = AccessTools.Method(typeof(EventFrame), nameof(Resync));
        int argument = Array.FindIndex(original.GetParameters(), p => p.ParameterType.FullName == "Barotrauma.Networking.IReadMessage");
        if (argument < 0) { throw new InvalidOperationException("entity event reader takes no message"); }
        if (!original.IsStatic) { argument++; }
        int marked = 0;
        for (int i = 0; i < code.Count; i++) {
            if (code[i].operand is not MethodInfo read || read.Name != "ReadVariableUInt32") { continue; }
            code.InsertRange(i + 1, new[] {
                new CodeInstruction(OpCodes.Dup),
                CodeInstruction.LoadArgument(argument),
                new CodeInstruction(OpCodes.Call, open)
            });
            marked++;
            i += 3;
        }
        if (marked != 1) { throw new InvalidOperationException($"entity event length is read {marked} times"); }
        int readEvent = code.FindIndex(c => c.operand is MethodInfo m && m.Name == "ReadEvent");
        int pad = readEvent < 0 ? -1 : code.FindIndex(readEvent, c => c.operand is MethodInfo m && m.Name == "ReadPadBits");
        if (pad < 0) { throw new InvalidOperationException("entity event read is not followed by a pad"); }
        code.InsertRange(pad + 1, new[] {
            CodeInstruction.LoadArgument(argument),
            new CodeInstruction(OpCodes.Call, resync)
        });
        return code;
    }
}
