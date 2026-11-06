using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace INet;

internal sealed class FragmentReceiver : IModule
{
    private static readonly ConditionalWeakTable<MessageDefragmenter, FragmentBuffer> Buffers = new();
    public void Attach(Harmony harmony)
    {
        if (MessageFragment.MaxSize != FragmentBuffer.FragmentBytes) { throw new InvalidOperationException("Native fragment size changed"); }
        harmony.Patch(AccessTools.Method(typeof(MessageDefragmenter), nameof(MessageDefragmenter.ProcessIncomingFragment)),
            prefix: new HarmonyMethod(typeof(FragmentReceiver), nameof(Read)));
    }
    public void RoundStart() { }
    public void Release() => Buffers.Clear();

    private static bool Read(MessageDefragmenter __instance, MessageFragment fragment, ref Option<ImmutableArray<byte>> __result)
    {
        FragmentBuffer buffer = Buffers.GetOrCreateValue(__instance);
        MessageFragment.Id id = fragment.FragmentId;
        FragmentResult result = buffer.Add(id.MessageId, id.FragmentIndex, id.FragmentCount, fragment.Data.AsSpan(), Environment.TickCount64, out byte[] message);
        __result = result == FragmentResult.Complete ? Option.Some(ImmutableCollectionsMarshal.AsImmutableArray(message)) : Option.None;
        return false;
    }
}
