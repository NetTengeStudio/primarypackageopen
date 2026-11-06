using System;
using Barotrauma.Networking;

namespace INet;

static class MessageCapacity
{
    public static void Reserve(IWriteMessage message, int payloadBytes)
    {
        if (payloadBytes <= 0 || message is not WriteOnlyMessage) { return; }
        int length = message.LengthBits;
        int extra = Math.Min(payloadBytes, NativeWire.MaxFrameBytes) * 8;
        if (length > int.MaxValue - extra || (long)message.Buffer.Length * 8 >= (long)length + extra) { return; }
        message.LengthBits = length + extra;
        message.LengthBits = length;
    }
}
