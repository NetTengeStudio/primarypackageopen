"""Reproducible static guardrails and source evidence. Not a multiplayer acceptance test."""
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
GAME = ROOT.parents[1]
ENGINE = Path('C:/Users/Admin/Documents/LuaCsForBarotrauma/Barotrauma')
OUT = GAME / 'Docs/PrimaryPackage/LiveEditor'

def read(relative):
    return (ROOT / relative).read_text(encoding='utf-8-sig')

editor = read('CSharp/Client/Interactions/LiveEditor.cs')
schema = read('CSharp/Shared/Interactions/DeviceEditor.cs')
client = read('CSharp/Client/Interactions/DeviceClient.cs')
server = read('CSharp/Server/Interactions/DeviceServer.cs')
transform = read('CSharp/Shared/Interactions/LiveItemTransform.cs')
journal = read('CSharp/Shared/Interactions/WorldEdits.cs')
runtime = read('CSharp/Client/GUI/GUIRuntime.cs')
assert 'MapEntity.MapEntityList' not in editor, 'picking must use indexed bounds'
assert 'DeviceEditor.Fields' not in client + server
assert 'GetAttribute<Serialize>()' in schema and 'InGameEditable' in schema
assert 'PropertyInfo.SetMethod?.IsPublic' in schema and 'registered.Contains(type)' in schema
assert 'StaticBodyConfig' in transform and 'StaticFixtures.Count' in transform
assert 'StaticFixtureTranslation.Validate' in transform and 'fixtures.Apply()' in transform
fixtures = read('CSharp/Shared/Interactions/StaticFixtureTranslation.cs')
assert 'owner.World.IsLocked' in fixtures and 'owner.Remove(shape.Fixture)' in fixtures and 'owner.Add(shape.Fixture' in fixtures
assert 'item.FindHull()' in transform
assert 'deviceBindings.TryGetValue(token' in client and 'Entity.FindEntityByID(item.ID) != item' in client
assert 'lease.Current' in server and 'PendingDeviceOffers' in server
assert 'session.WorldReadyRequested = true' in server
assert 'RequireEditingClients' in server and 'AllowEditedWorldSync' in server
assert 'CancelOnWindowOpen' in runtime and 'cancelOnWindowOpen: false' in editor
translation = read('CSharp/Server/Interactions/WorldTranslationServer.cs')
assert 'BindTranslation(pending.Mutations)' in translation and 'ValidateTranslation(pending.Mutations)' in translation
assert 'TranslationOutboxCapacity = 32' in translation and 'if (!accepted) { return false; }' in translation
assert 'TranslationWire.WriteIntent' in read('CSharp/Client/Interactions/WorldTranslationClient.cs')
assert 'WorldCapabilities.BatchTranslation' in read('CSharp/Shared/Interactions/WorldCapabilities.cs')
assert 'Faulted = true' in journal
assert 'PreviousRevision' in journal and 'RequireCurrent' in journal and 'WorldEditChangeKind.Undo' in journal
for relative in ('CSharp/Client/Interactions/LiveEditor.cs', 'CSharp/Client/Interactions/PhantomRenderer.cs',
                 'CSharp/Client/Interactions/DirectorPlayback.cs', 'CSharp/Shared/Interactions/HorrorSignal.cs'):
    code = read(relative)
    assert all(value not in code for value in ('Thread.Sleep', 'Task.Delay', 'SubEditorScreen.Select(', 'new Submarine(')), relative
content = GAME / 'LocalMods/contentclient/CSharp/Client'
assert 'Thread.Sleep' not in (content / 'ResidentAsset.cs').read_text(encoding='utf-8-sig')
assert 'if (!ResidentAsset.Rehydrate(info))' in (content / 'LoadPatches.cs').read_text(encoding='utf-8-sig')
assert 'foreach (Submarine submarine in Submarine.Loaded)' in (GAME / 'LocalMods/contentserver/CSharp/Server/Entitlement.cs').read_text(encoding='utf-8-sig')

sources = {
    'BarotraumaShared/SharedSource/Map/MapEntityPrefab.cs': ['public static IEnumerable<MapEntityPrefab> List', 'public static MapEntityPrefab Find'],
    'BarotraumaShared/SharedSource/Serialization/ISerializableEntity.cs': ['SerializableProperties'],
    'BarotraumaShared/SharedSource/Items/Components/DockingPort.cs': ['CreateJoint', 'public void Dock'],
    'BarotraumaShared/SharedSource/Items/Components/Signal/Wire.cs': ['SetConnectedDirty', 'FixNodeEnds'],
    'BarotraumaShared/SharedSource/Map/Entity.cs': ['DetermineID', 'FindFreeId', 'CreationIndex'],
    'BarotraumaShared/SharedSource/Utils/IdRemap.cs': ['DetermineNewOffset', 'GetOffsetId'],
    'BarotraumaShared/SharedSource/Map/MapEntity.cs': ['Clone', 'MapLoaded', 'OnMapLoaded'],
    'BarotraumaShared/SharedSource/Map/Submarine.cs': ['Loading = true', 'new SubmarineBody', 'SaveToXElement'],
    'BarotraumaShared/SharedSource/Map/SubmarineBody.cs': ['GenerateConvexHull', 'StaticFixtures.Add', 'Borders ='],
    'BarotraumaShared/SharedSource/Map/Hull.cs': ['public override Rectangle Rect', 'Item.UpdateHulls', 'waveY = new'],
    'BarotraumaShared/SharedSource/Map/Gap.cs': ['FindHulls', 'outsideCollisionBlocker'],
    'BarotraumaShared/SharedSource/Map/Structure.cs': ['CreateSections', 'UpdateSections'],
    'BarotraumaShared/SharedSource/Map/WayPoint.cs': ['WayPointList', 'FindHull'],
    'BarotraumaShared/SharedSource/Items/Item.cs': ['public override void Move', 'public Hull FindHull', 'public static Item Load'],
    'BarotraumaShared/SharedSource/Items/Components/Signal/ConnectionPanel.cs': ['MoveConnectedWires', 'DisconnectedWires'],
    'BarotraumaServer/ServerSource/Items/Components/Signal/ConnectionPanel.cs': ['ServerEventRead', 'CheckCharacterSuccess'],
    'BarotraumaShared/SharedSource/Serialization/SerializableProperty/SerializableProperty.cs': ['GetProperties', 'PropertyInfo.SetValue'],
    'BarotraumaShared/SharedSource/Serialization/Editable/Editable.cs': ['class Editable', 'class InGameEditable'],
    'BarotraumaClient/ClientSource/Serialization/SerializableEntityEditor.cs': ['FallBackTextTag', 'propertyTag'],
    'BarotraumaClient/ClientSource/Screens/SubEditorScreen.cs': ['transformWidget', 'GenerateWaypoints'],
    'BarotraumaClient/ClientSource/SubEditorCommands.cs': ['class TransformCommand', 'entity.Rect ='],
    'BarotraumaShared/SharedSource/Networking/EntitySpawner.cs': ['enum SpawnableType', 'class SubmarineSpawnInfo'],
    'BarotraumaServer/ServerSource/Networking/EntitySpawner.cs': ['case Item item:', 'case Character character:'],
    'BarotraumaServer/ServerSource/Networking/NetEntityEvent/ServerEntityEventManager.cs': ['uniqueEvents', 'InitClientMidRoundSync'],
    'BarotraumaClient/ClientSource/Networking/NetEntityEvent/ClientEntityEventManager.cs': ['LastReceivedID', 'MidRoundSyncing'],
    'BarotraumaServer/ServerSource/Networking/GameServer.cs': ['SetClientCharacter', 'SendDirectChatMessage'],
    'BarotraumaServer/ServerSource/Networking/RespawnManager.cs': ['RespawnCharacters', 'ApplyWalletData'],
    'BarotraumaShared/SharedSource/Characters/Character.cs': ['public void Revive', 'info?.Remove()'],
    'BarotraumaShared/SharedSource/Characters/CharacterInfo.cs': ['public void Remove()', 'Character = null;'],
    'BarotraumaShared/SharedSource/Networking/Primitives/NetworkExtensions.cs': ['NetDeliveryMethod.ReliableOrdered'],
}
evidence = []
for relative, anchors in sources.items():
    path = ENGINE / relative
    raw = path.read_bytes()
    text = raw.decode('utf-8-sig')
    locations = []
    for anchor in anchors:
        at = text.find(anchor)
        assert at >= 0, f'engine source changed: {relative}: {anchor}'
        locations.append({'symbol': anchor, 'line': text.count('\n', 0, at) + 1})
    evidence.append({'path': str(path), 'sha256': hashlib.sha256(raw).hexdigest(), 'anchors': locations})

enabled = [p.attrib.get('path', '') for p in ET.parse(GAME / 'config_player.xml').iter('package')]
active_client = [p for p in enabled if '3831040655' in p or 'contentclient' in p.lower()]
active_client_root = Path('C:/Users/Admin/AppData/Local/Daedalic Entertainment GmbH/Barotrauma/WorkshopMods/Installed/3831040655')
active_client_matches = all((active_client_root / 'CSharp/Client' / name).read_bytes() == (content / name).read_bytes() for name in ('LoadPatches.cs', 'ResidentAsset.cs'))
assert active_client_matches, 'active contentclient must contain the verified load fixes'
assert 'Camera reads raw pointer coordinates' in read('CSharp/Client/GUI/GUIWorldInput.cs')
assert 'allowZoom = false' in read('CSharp/Client/GUI/GUIWorldInput.cs')
OUT.mkdir(parents=True, exist_ok=True)
result = {'status': 'PASS', 'scope': 'static guardrails and hashes/anchors of inspected engine paths; not live simulation or network acceptance',
          'engineSources': evidence, 'activeContentClient': active_client, 'activeContentClientMatchesVerifiedFixes': active_client_matches,
          'localContentClientEnabled': any('LocalMods/contentclient/filelist.xml' == p.replace('\\', '/') for p in enabled)}
(OUT / 'source-evidence.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({key: result[key] for key in ('status', 'scope', 'activeContentClient', 'localContentClientEnabled')}, ensure_ascii=False))
