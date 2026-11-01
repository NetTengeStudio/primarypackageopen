using System;
using System.Globalization;
using System.Xml.Linq;
using PrimaryPackage.Core;

namespace PrimaryPackage.TextMotion;

internal static class TypeSettings {
    private const string Path = "Config/TextMotionClient.xml";
    internal static TypePace Pace { get; private set; } = TypePace.Normal;
    internal static float ClickGain { get; private set; } = 0.04f;
    private static DebugConsole.Command speedCommand, volumeCommand;

    internal static void Attach() {
        ModPrefs.Read(Path, root => {
            float speed = (float?)root.Attribute("speed") ?? 1;
            float gain = (float?)root.Attribute("volume") ?? 0.04f;
            Pace = new TypePace { Speed = float.IsFinite(speed) ? Math.Clamp(speed, TypePace.MinSpeed, TypePace.MaxSpeed) : 1,
                Instant = (bool?)root.Attribute("instant") ?? false };
            ClickGain = float.IsFinite(gain) ? Math.Clamp(gain, 0, 0.15f) : 0.04f;
        });
        speedCommand = ModCommands.Register("pp_typespeed|scpitd_typespeed", "Text speed: 0.25..4 | instant | normal", Speed, true, false);
        volumeCommand = ModCommands.Register("pp_typevolume|scpitd_typevolume", "Typewriter volume: 0..0.15 | normal", Volume, true, false);
    }

    internal static void Release() {
        ModCommands.Drop(speedCommand); ModCommands.Drop(volumeCommand);
        speedCommand = volumeCommand = null;
        Pace = TypePace.Normal; ClickGain = 0.04f;
    }

    private static bool Save(in TypePace pace, float gain) => ModPrefs.Write(Path, new XElement("TextMotion",
        new XAttribute("speed", pace.Clamped), new XAttribute("instant", pace.Instant), new XAttribute("volume", gain)));

    private static void Speed(string[] args) {
        if (args.Length == 0) {
            ModLog.Message(Pace.Instant ? "text appears instantly" : "text prints at x" + Pace.Clamped.ToString("0.##", CultureInfo.InvariantCulture));
            return;
        }
        TypePace next = Pace;
        string value = args[0].Trim();
        if (value.Equals("instant", StringComparison.OrdinalIgnoreCase)) { next.Instant = true; }
        else if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)) { next = TypePace.Normal; }
        else if (Number(value, TypePace.MinSpeed, TypePace.MaxSpeed, out float speed)) { next.Speed = speed; next.Instant = false; }
        else { ModLog.Message("pp_typespeed 0.25..4 | instant | normal"); return; }
        if (Save(next, ClickGain)) { Pace = next; TypeSound.Warm(); }
        Speed(Array.Empty<string>());
    }

    private static void Volume(string[] args) {
        if (args.Length == 0) { ModLog.Message("typewriter click gain " + ClickGain.ToString("0.###", CultureInfo.InvariantCulture)); return; }
        float gain;
        if (args[0].Equals("normal", StringComparison.OrdinalIgnoreCase)) { gain = 0.04f; }
        else if (!Number(args[0], 0, 0.15f, out gain)) { ModLog.Message("pp_typevolume 0..0.15 | normal"); return; }
        if (Save(Pace, gain)) { ClickGain = gain; TypeSound.Warm(); }
        Volume(Array.Empty<string>());
    }

    private static bool Number(string value, float min, float max, out float number) =>
        float.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
        float.IsFinite(number) && number >= min && number <= max;
}
