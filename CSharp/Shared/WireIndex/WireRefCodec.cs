using System;
using Barotrauma.Networking;

namespace PrimaryPackage.Netcode;

public static class WireRefCodec {
    public const int WireBits = ComponentAddress.WireBits + 1;

    public static void Write<TKeys>(IWriteMessage msg, int index, in TKeys keys) where TKeys : struct, IKeySequence {
        bool addressed = ComponentAddress.TryAt(keys, index, out ComponentAddress address);
        msg.WriteBoolean(addressed);
        if (addressed) { address.Write(msg); }
    }

    public static int Read<TKeys>(IReadMessage msg, in TKeys keys) where TKeys : struct, IKeySequence =>
        msg.ReadBoolean() ? ComponentAddress.Read(msg).IndexIn(keys) : -1;

    public static int Peek<TKeys>(IReadMessage msg, in TKeys keys) where TKeys : struct, IKeySequence {
        int position = msg.BitPosition;
        int index = Read(msg, keys);
        msg.BitPosition = position;
        return index;
    }
}
