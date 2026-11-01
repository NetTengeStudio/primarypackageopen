using System;
using Barotrauma.Networking;

namespace PrimaryPackage.Interactions;

internal sealed class GMAnnouncement {
    internal Guid Id;
    internal string Target = "", Text = "";
    internal byte Format = 3, R = 255, G = 255, B = 255, A = 255;
    internal float Hold = 5, FadeIn = .2f, FadeOut = .5f;
    internal const byte Toast = 255;
    internal void Validate() {
        if (Id == Guid.Empty || Target == null || Target.Length > 128 || !InteractionProtocol.ValidText(Text, Format == Toast ? 512 : 200) ||
            Format is not (3 or 7 or 10 or 11 or Toast) || !float.IsFinite(Hold) || Hold < .25f || Hold > 60 ||
            !float.IsFinite(FadeIn) || FadeIn < 0 || FadeIn > 5 || !float.IsFinite(FadeOut) || FadeOut < 0 || FadeOut > 5) { throw new ArgumentException("Недопустимый формат, текст или длительность GM-сообщения"); }
    }
    internal void Write(IWriteMessage output) {
        output.WriteString(Id.ToString("N")); output.WriteString(Target); output.WriteString(Text); output.WriteByte(Format);
        output.WriteByte(R); output.WriteByte(G); output.WriteByte(B); output.WriteByte(A);
        output.WriteSingle(Hold); output.WriteSingle(FadeIn); output.WriteSingle(FadeOut);
    }
    internal static GMAnnouncement Read(IReadMessage input) {
        var value = new GMAnnouncement { Id = Guid.ParseExact(input.ReadString(), "N"), Target = input.ReadString(), Text = input.ReadString(), Format = input.ReadByte(),
            R = input.ReadByte(), G = input.ReadByte(), B = input.ReadByte(), A = input.ReadByte(), Hold = input.ReadSingle(), FadeIn = input.ReadSingle(), FadeOut = input.ReadSingle() };
        if (input.BitPosition > input.LengthBits) { throw new ArgumentException("Оборван пакет GM-сообщения"); }
        value.Validate(); return value;
    }
}
