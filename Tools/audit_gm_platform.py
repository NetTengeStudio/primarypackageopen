"""Content and lifecycle assertions for the offline GM platform verification."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
catalog = ET.parse(root / 'Content/Systems/RPWheel/Definitions/emotes.xml').getroot()
sounds = root / 'Content/Systems/RPWheel/Emotions/Sounds'
manifest = json.loads((sounds / 'manifest.json').read_text(encoding='utf-8'))
definitions = list(catalog)
assert 0 < len(definitions) <= 64
assert len({e.attrib['id'] for e in definitions}) == len(definitions)
used = set()
for definition in definitions:
    for sound in definition.findall('Sound'):
        name = sound.attrib['name']
        assert '/' not in name and '\\' not in name and '..' not in name
        info = manifest[name]
        assert info['seconds'] > 0
        beats = info.get('onsets', [])
        assert beats == sorted(beats) and all(0 <= beat <= info['seconds'] for beat in beats)
        assert (sounds / (name + '.ogg')).is_file()
        used.add(name)
assert used == {p.stem for p in sounds.glob('*.ogg')}, 'orphaned or missing imported sounds'
for record in json.loads((root / 'Docs/emotion-import.json').read_text(encoding='utf-8')):
    source = Path(record['source'])
    assert hashlib.sha256(source.read_bytes()).hexdigest() == record['sha256'], 'import input changed: ' + str(source)
    if source.suffix == '.ogg':
        assert hashlib.sha256((sounds / source.name).read_bytes()).hexdigest() == record['sha256'], 'copied audio changed'
for relative in ('CSharp/Client/Interactions/PhantomRenderer.cs', 'CSharp/Shared/Interactions/PhantomMotion.cs'):
    text = (root / relative).read_text(encoding='utf-8')
    assert all(forbidden not in text for forbidden in ('Character.Create(', 'new Character(', 'Entity.Spawner', 'Thread.Sleep', 'Task.Delay'))
for relative in ('CSharp/Client/Interactions/InteractionClient.cs', 'CSharp/Client/RPWheel/rpwheelmod.cs', 'CSharp/Server/RPWheel/rpwheelserver.cs'):
    text = (root / relative).read_text(encoding='utf-8')
    assert 'IEventRoundEnded' in text and 'OnRoundEnd() => RoundStart()' in text
print(json.dumps({'status': 'PASS', 'emotionDefinitions': len(definitions), 'verifiedUnmodifiedSounds': len(used), 'scope': 'content hashes, catalog references, static lifecycle and phantom guardrails; not live gameplay'}, ensure_ascii=False))
