using System;
using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

internal sealed partial class GMAnnouncement {
    internal void Write(IWriteMessage output) {
        output.WriteString(Id.ToString("N")); output.WriteString(Target); output.WriteString(Text); output.WriteByte(Format);
        output.WriteByte((byte)Sender); output.WriteString(SenderAccount); output.WriteString(CustomSender);
        output.WriteByte(R); output.WriteByte(G); output.WriteByte(B); output.WriteByte(A);
        output.WriteSingle(Hold); output.WriteSingle(FadeIn); output.WriteSingle(FadeOut);
    }
    internal static GMAnnouncement Read(IReadMessage input) {
        var value = new GMAnnouncement { Id = Guid.ParseExact(input.ReadString(), "N"), Target = input.ReadString(), Text = input.ReadString(), Format = input.ReadByte(),
            Sender = (GMMessageSender)input.ReadByte(), SenderAccount = input.ReadString(), CustomSender = input.ReadString(),
            R = input.ReadByte(), G = input.ReadByte(), B = input.ReadByte(), A = input.ReadByte(), Hold = input.ReadSingle(), FadeIn = input.ReadSingle(), FadeOut = input.ReadSingle() };
        if (input.BitPosition > input.LengthBits) { throw new ArgumentException("Оборван пакет GM-сообщения"); }
        value.Validate(); return value;
    }
}

internal sealed partial class GMAnnouncementReceipt {
    internal void Write(IWriteMessage output) {
        output.WriteString(Id.ToString("N")); output.WriteString(Sender); output.WriteString(Error); output.WriteInt32(Recipients); output.WriteInt64(Timestamp);
    }
    internal static GMAnnouncementReceipt Read(IReadMessage input) {
        var receipt = new GMAnnouncementReceipt { Id = Guid.ParseExact(input.ReadString(), "N"), Sender = input.ReadString(), Error = input.ReadString(), Recipients = input.ReadInt32(), Timestamp = input.ReadInt64() };
        if (input.BitPosition > input.LengthBits) { throw new ArgumentException("Оборвано подтверждение GM-сообщения"); }
        receipt.Validate(); return receipt;
    }
}
