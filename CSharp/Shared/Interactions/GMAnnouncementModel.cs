using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PrimaryPackage.Interactions;

internal enum GMMessageSender : byte { None, GM, Server, Self, Player, Custom }

// The request contains a sender choice, never a client-authoritative resolved name.
internal sealed partial class GMAnnouncement {
    public Guid Id { get; set; }
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public GMMessageSender Sender { get; set; } = GMMessageSender.GM;
    public string SenderAccount { get; set; } = "";
    public string CustomSender { get; set; } = "";
    public byte Format { get; set; } = 3;
    public byte R { get; set; } = 255;
    public byte G { get; set; } = 255;
    public byte B { get; set; } = 255;
    public byte A { get; set; } = 255;
    public float Hold { get; set; } = 5;
    public float FadeIn { get; set; } = .2f;
    public float FadeOut { get; set; } = .5f;
    internal const byte PlainMsg = 254, Toast = 255;
    internal const byte AllFormats = 63, ChatFormats = 3;
    internal static byte FormatBit(byte format) => format switch { PlainMsg => 1, 3 => 2, 11 => 4, 10 => 8, 7 => 16, Toast => 32, _ => 0 };
    internal bool HasSender => Format is not (PlainMsg or Toast);
    internal byte NativeFormat => Format == PlainMsg ? (byte)3 : Format;
    // Native popup types ignore SenderName. Present the chosen name in their text,
    // using the same formatting for preview and delivery, while keeping native type 11.
    internal string NativeText(string sender) => Format is 7 or 10 or 11 && !string.IsNullOrEmpty(sender) ? sender + ":\n" + Text : Text;
    internal static bool ValidFormat(byte format) => format is 3 or 7 or 10 or 11 or PlainMsg or Toast;
    internal void Validate() {
        if (Id == Guid.Empty || Target == null || Target.Length > 128 || !InteractionText.Valid(Text, Format == Toast ? 512 : 200) ||
            !ValidFormat(Format) || !Enum.IsDefined(Sender) || SenderAccount == null || SenderAccount.Length > 128 || CustomSender == null ||
            CustomSender.Length > 64 || CustomSender.Length > 0 && !InteractionText.Valid(CustomSender, 64) ||
            HasSender && Sender == GMMessageSender.Player && string.IsNullOrWhiteSpace(SenderAccount) ||
            HasSender && Sender == GMMessageSender.Custom && string.IsNullOrWhiteSpace(CustomSender) ||
            !float.IsFinite(Hold) || Hold < .25f || Hold > 60 || !float.IsFinite(FadeIn) || FadeIn < 0 || FadeIn > 5 ||
            !float.IsFinite(FadeOut) || FadeOut < 0 || FadeOut > 5) {
            throw new ArgumentException("Проверьте формат, отправителя, длину текста и длительность GM-сообщения");
        }
    }
    internal string ResolveSender(string server, string self, string player) {
        if (!HasSender) { return ""; }
        string name = Sender switch {
            GMMessageSender.None => "", GMMessageSender.GM => "GM", GMMessageSender.Server => server,
            GMMessageSender.Self => self, GMMessageSender.Player => player, GMMessageSender.Custom => CustomSender,
            _ => throw new ArgumentException("Неизвестный отправитель")
        };
        if (name == null || name.Length > 0 && !InteractionText.Valid(name, 128) || name.Length == 0 && Sender != GMMessageSender.None) {
            throw new ArgumentException("Имя отправителя недоступно или содержит недопустимые символы");
        }
        return name;
    }
    internal GMAnnouncement Copy() => (GMAnnouncement)MemberwiseClone();
}

internal sealed partial class GMAnnouncementReceipt {
    public Guid Id { get; set; }
    public string Sender { get; set; } = "";
    public string Error { get; set; } = "";
    public int Recipients { get; set; }
    public long Timestamp { get; set; }
    internal bool Accepted => Error.Length == 0;
    internal void Validate() {
        if (Id == Guid.Empty || Sender == null || Sender.Length > 0 && !InteractionText.Valid(Sender, 128) || Error == null ||
            Error.Length > 512 || Recipients is < 0 or > 128 || Timestamp <= 0 || Timestamp > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            Error.Length > 0 && (Recipients != 0 || Sender.Length != 0)) { throw new InvalidDataException("Некорректное подтверждение GM-сообщения"); }
    }
    internal GMAnnouncementReceipt Copy() => (GMAnnouncementReceipt)MemberwiseClone();
}

// Raw input is intentional: closing the panel must preserve an unfinished number or an empty draft.
internal sealed class GMMessageDraft {
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public byte Format { get; set; } = 3;
    public GMMessageSender Sender { get; set; } = GMMessageSender.GM;
    public string SenderAccount { get; set; } = "";
    public string CustomSender { get; set; } = "";
    public string R { get; set; } = "255";
    public string G { get; set; } = "255";
    public string B { get; set; } = "255";
    public string A { get; set; } = "255";
    public string Hold { get; set; } = "5";
    public string FadeIn { get; set; } = "0.2";
    public string FadeOut { get; set; } = "0.5";
    internal void Validate() {
        if (!Bounded(Target, 128) || !Bounded(Text, 512) || !GMAnnouncement.ValidFormat(Format) || !Enum.IsDefined(Sender) ||
            !Bounded(SenderAccount, 128) || !Bounded(CustomSender, 64) || !Bounded(R, 3) || !Bounded(G, 3) || !Bounded(B, 3) ||
            !Bounded(A, 3) || !Bounded(Hold, 12) || !Bounded(FadeIn, 12) || !Bounded(FadeOut, 12)) { throw new InvalidDataException("Недопустимый черновик GM-сообщения"); }
    }
    private static bool Bounded(string value, int limit) => value != null && value.Length <= limit;
    internal GMMessageDraft Copy() => (GMMessageDraft)MemberwiseClone();
    internal GMAnnouncement Create() {
        Validate();
        if (!byte.TryParse(R, NumberStyles.None, CultureInfo.InvariantCulture, out byte r) || !byte.TryParse(G, NumberStyles.None, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(B, NumberStyles.None, CultureInfo.InvariantCulture, out byte b) || !byte.TryParse(A, NumberStyles.None, CultureInfo.InvariantCulture, out byte a)) {
            throw new ArgumentException("RGBA: введите целые числа от 0 до 255");
        }
        bool toast = Format == GMAnnouncement.Toast;
        bool named = Format is not (GMAnnouncement.PlainMsg or GMAnnouncement.Toast);
        var result = new GMAnnouncement { Id = Guid.NewGuid(), Target = Target, Text = Text, Format = Format, Sender = named ? Sender : GMMessageSender.None,
            SenderAccount = named && Sender == GMMessageSender.Player ? SenderAccount : "", CustomSender = named && Sender == GMMessageSender.Custom ? CustomSender : "",
            R = r, G = g, B = b, A = a, Hold = toast ? Time(Hold) : 5, FadeIn = toast ? Time(FadeIn) : .2f, FadeOut = toast ? Time(FadeOut) : .5f };
        result.Validate(); return result;
    }
    private static float Time(string value) => float.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float result) && float.IsFinite(result)
        ? result : throw new ArgumentException("Введите допустимое время в секундах");
    internal static GMMessageDraft From(GMAnnouncement value) => new() { Target = value.Target, Text = value.Text, Format = value.Format, Sender = value.Sender,
        SenderAccount = value.SenderAccount, CustomSender = value.CustomSender, R = value.R.ToString(CultureInfo.InvariantCulture), G = value.G.ToString(CultureInfo.InvariantCulture),
        B = value.B.ToString(CultureInfo.InvariantCulture), A = value.A.ToString(CultureInfo.InvariantCulture), Hold = value.Hold.ToString("R", CultureInfo.InvariantCulture),
        FadeIn = value.FadeIn.ToString("R", CultureInfo.InvariantCulture), FadeOut = value.FadeOut.ToString("R", CultureInfo.InvariantCulture) };
}

internal sealed class GMMessageHistoryEntry {
    public GMAnnouncement Message { get; set; }
    public GMAnnouncementReceipt Receipt { get; set; }
    internal GMMessageHistoryEntry Copy() => new() { Message = Message.Copy(), Receipt = Receipt.Copy() };
}

internal sealed class GMMessageState {
    internal const int HistoryCapacity = 32;
    public GMMessageDraft Draft { get; set; } = new();
    public List<GMMessageHistoryEntry> History { get; set; } = new();
    internal void Validate() {
        if (Draft == null || History == null || History.Count > HistoryCapacity) { throw new InvalidDataException("Недопустимая история GM-сообщений"); }
        Draft.Validate();
        var ids = new HashSet<Guid>();
        foreach (var entry in History) {
            if (entry?.Message == null || entry.Receipt == null) { throw new InvalidDataException("Пустое GM-сообщение в истории"); }
            try { entry.Message.Validate(); entry.Receipt.Validate(); }
            catch (ArgumentException error) { throw new InvalidDataException("Повреждённое GM-сообщение в истории", error); }
            if (!entry.Receipt.Accepted || entry.Message.Id != entry.Receipt.Id || !ids.Add(entry.Message.Id)) { throw new InvalidDataException("Повтор или неподтверждённое GM-сообщение в истории"); }
        }
    }
    internal bool Record(GMAnnouncement message, GMAnnouncementReceipt receipt) {
        message.Validate(); receipt.Validate();
        if (!receipt.Accepted || message.Id != receipt.Id) { throw new ArgumentException("Подтверждение не соответствует GM-сообщению"); }
        foreach (var entry in History) { if (entry.Message.Id == message.Id) { return false; } }
        if (History.Count == HistoryCapacity) { History.RemoveAt(0); }
        History.Add(new() { Message = message.Copy(), Receipt = receipt.Copy() });
        return true;
    }
    internal GMMessageState Copy() {
        var copy = new GMMessageState { Draft = Draft.Copy() };
        foreach (var entry in History) { copy.History.Add(entry.Copy()); }
        return copy;
    }
}
