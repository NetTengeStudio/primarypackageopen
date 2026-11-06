using System;
using Barotrauma.Networking;

namespace INet;

public static class WireBounds {
    public const int Min = -1;
    public const int Max = 65535;

    public static void Write(IWriteMessage msg, int value, int min, int max) =>
        msg.WriteRangedInteger(Clamp(value, Min, Max), Min, Max);

    public static int Read(IReadMessage msg, int min, int max) =>
        Clamp(msg.ReadRangedInteger(Min, Max), min, max);

    public static int Clamp(int value, int min, int max) {
        if (max < min) { return min; }
        if (value < min) { return min; }
        return value > max ? max : value;
    }
}
