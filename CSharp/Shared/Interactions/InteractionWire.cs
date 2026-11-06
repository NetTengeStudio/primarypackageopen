using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

internal static partial class InteractionProtocol {
    public static void Header(IWriteMessage message, InteractionPacket packet) { message.WriteByte(Version); message.WriteByte((byte)packet); }
    public static bool Read(IReadMessage message, out InteractionPacket packet) {
        packet = default;
        if (message.LengthBits - message.BitPosition < 16 || message.ReadByte() != Version) { return false; }
        byte value = message.ReadByte();
        if (value > (byte)InteractionPacket.Flicker) { return false; }
        packet = (InteractionPacket)value;
        return true;
    }
}
