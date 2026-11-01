"""Reproduce the authorized SCPITD emotion import; GM Menu is never an input."""
from pathlib import Path
import hashlib
import json

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / "scpitdcode"
ASSETS = ROOT.parent / "scpitd-code-side"
records = []


def read(relative):
    path = SOURCE / relative
    raw = path.read_bytes()
    records.append({"source": str(path), "sha256": hashlib.sha256(raw).hexdigest()})
    return raw.decode("utf-8-sig").replace("\r\n", "\n")


def write(relative, text):
    path = ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


for name, source in [("EmotionCatalog.cs", "CSharp/Shared/Rp/Emotes.cs"), ("Poses.cs", "CSharp/Shared/Rp/Poses.cs"), ("IkBlend.cs", "CSharp/Shared/Core/IkBlend.cs")]:
    text = read(source).replace("using ScpItdMod.Core;", "using Vec2 = System.Numerics.Vector2;")
    text = text.replace("namespace ScpItdMod.Rp", "namespace PrimaryPackage.Emotions").replace("namespace ScpItdMod.Core", "namespace PrimaryPackage.Emotions")
    if name == "IkBlend.cs":
        text = "using Vec2 = System.Numerics.Vector2;\n" + text
    if name == "EmotionCatalog.cs":
        text = text.replace('Content/Sound/Emotes/', 'Content/Systems/RPWheel/Emotions/Sounds/')
    if name == "Poses.cs":
        text = text.replace('if (n >= Capacity) { return; }', '')
        # Coincident keys carry the later authored target; time is never silently shifted.
        text = text.replace('if (n > 0 && time <= t[n - 1] + 1e-4f) { time = t[n - 1] + 1e-4f; }', '''if (!float.IsFinite(time) || !float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(vel.X) || !float.IsFinite(vel.Y) || time < 0 || n > 0 && time < t[n - 1]) { throw new ArgumentException("Invalid hand trajectory key"); }
            if (n > 0 && time == t[n - 1]) { n--; }
            if (n >= Capacity) { throw new InvalidOperationException("Hand trajectory capacity exceeded"); }''')
        text = text.replace('right.Key(beat + 0.12f, raised + new Vec2(0.0f, -a * 0.05f));', 'if (beat + 0.12f < end) { right.Key(beat + 0.12f, raised + new Vec2(0.0f, -a * 0.05f)); }')
        text = text.replace('right.Key(beat + 0.18f, hit + new Vec2(a * 0.08f, 0.0f));', 'if (beat + 0.18f < end) { right.Key(beat + 0.18f, hit + new Vec2(a * 0.08f, 0.0f)); }')
        text = text.replace('Beats(right, beats, door, back);', 'Beats(right, beats, door, back, end);')
        text = text.replace('Vec2 hit, Vec2 back) {', 'Vec2 hit, Vec2 back, float end) {')
        text = text.replace(' : bt + 0.3f;', ' : end;').replace(' : bt + 0.28f;', ' : end;').replace(' : bt + 0.2f;', ' : end;')
        text = text.replace('right.Key(mid, openR);\n                        left.Key(mid, openL);', 'if (mid > bt && mid < end) { right.Key(mid, openR); left.Key(mid, openL); }')
        text = text.replace('h.Key((bt + next) * 0.5f, back);', 'if (next > bt) { h.Key((bt + next) * 0.5f, back); }')
        # Do not overwrite a final impact coincident with the end of the measured sound.
        import re
        text = re.sub(r'(right|left)\.Hold\(end, ([^;]+)\);', r'if (end > \1.End) { \1.Hold(end, \2); }', text)
    if name == "Poses.cs":
        text = text.replace("        public const float StanceRate = 5.0f;\n", "")
        start = text.index("        public static void HandsUp(")
        end = text.index("        public static void Build(", start)
        text = text[:start] + text[end:]
    write("CSharp/Shared/Emotions/" + name, text)

text = read("CSharp/Client/Rp/EmoteView.cs")
text = text.replace("using ScpItdMod.Core;", "using PrimaryPackage.Core;\nusing Vec2 = System.Numerics.Vector2;").replace("using ScpItdMod.Hud;", "")
text = text.replace("namespace ScpItdMod.Rp", "namespace PrimaryPackage.Emotions").replace("EmoteView", "EmotePlayback")
text = text.replace("Catalog = EmoteData.Load();", "Catalog = RPWheel.EmoteCatalog.Source;")
text = text.replace("        public static void Reset()", "        internal static void RefreshCatalog() { Reset(); Catalog = RPWheel.EmoteCatalog.Source; }\n        public static void Reset()")
text = text.replace('Speech.Say(actor, "*" + def.Bubble + "*")', 'RPWheel.RPWheelMod.EmoteDisplay?.Show(actor, def.Bubble)')
text = text.replace('if (!SoundStore.Ready) { return false; }', 'if (GameMain.SoundManager == null) { return false; }').replace("SoundStore.File(s.Path)", "SoundStore.Get(s.Path)")
# Surrender belongs to another SCPITD module and is not represented by a fake state in Primary.
start = text.index('        private sealed class Stance {')
end = text.index('        private static AnimController', start)
text = text[:start] + text[end:]
text = text.replace('        private static readonly Dictionary<Character, Stance> stances = new Dictionary<Character, Stance>();\n', '').replace('            stances.Clear();\n', '')
start = text.index('            if (stances.Count > 0) {')
end = text.index('            if (runs.Count == 0)', start)
text = text[:start] + text[end:]
start = text.index('            if (stances.Count == 0 && !RpWorld.IsSurrendered(c))')
end = text.index('\n        }', start)
text = text[:start] + text[end:]
text = text.replace('(runs.TryGetValue(actor, out Run run) && run.Def.Pose != EmotePose.None || stances.ContainsKey(actor))', 'runs.TryGetValue(actor, out Run run) && run.Def.Pose != EmotePose.None')
start = text.index('            } else if (stances.TryGetValue(c, out Stance stance))')
end = text.index('            } else { return; }', start)
text = text[:start] + text[end:]
text = text.replace('runs.Count == 0 && stances.Count == 0', 'runs.Count == 0')
start = text.index('            } else {\n                if (!stances.TryGetValue')
end = text.index('\n            if (!MathUtils.IsValid(pos))', start)
text = text[:start] + '            } else { return; }' + text[end:]
text = text.replace('if (pair.Key.IsDead && !r.Stopping)', 'if (EmoteRules.Interrupted(r.Def, EmotionFacts.Read(pair.Key, false, double.MaxValue)) && !r.Stopping)')
assert 'ScpItdMod' not in text and 'stances' not in text and 'RpWorld' not in text
write("CSharp/Client/Emotions/EmotePlayback.cs", text)

for source, target in [("Content/Data/emotes.xml", "Content/Systems/RPWheel/Definitions/emotes.xml")]:
    path = ASSETS / source
    raw = path.read_bytes()
    records.append({"source": str(path), "sha256": hashlib.sha256(raw).hexdigest()})
    target = ROOT / target
    target.write_bytes(raw)
for path in (ASSETS / 'Content/Sound/Emotes').iterdir():
    if path.suffix.lower() not in ('.ogg', '.json'):
        continue
    raw = path.read_bytes()
    records.append({"source": str(path), "sha256": hashlib.sha256(raw).hexdigest()})
    target = ROOT / 'Content/Systems/RPWheel/Emotions/Sounds' / path.name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(raw)
write('Docs/emotion-import.json', json.dumps(records, ensure_ascii=False, indent=2) + '\n')
print(f'Imported {len(records)} source/resource inputs from SCPITD; no GM Menu inputs')
