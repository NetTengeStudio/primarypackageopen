"""Bind fresh offline reports to the production/test source and emitted assembly hashes."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
game = root.parents[1]
out = game / 'Docs/PrimaryPackage/LiveEditor'

def load(name):
    return json.loads((out / name).read_text(encoding='utf-8-sig'))

def digest(path):
    return {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}

tests = load('tests.log')
content = load('content-tests.log')
physics = load('physics-tests.log')
sync_logs = load('event-graph-evidence.json')
assert physics['status'] == 'PASS'
client = load('contracts-client.log')
server = load('contracts-server.log')
assert tests['failed'] == 0 and content['status'] == 'PASS'
assert client['devices'] == server['devices'], 'client/server schema disagreement'
for folder, targets in (('build', ('client', 'server')), ('contentclient-build', ('client',)), ('contentserver-build', ('server',)), ('active-contentclient-build', ('client',))):
    report = (out / folder / 'report.txt').read_text(encoding='utf-8-sig')
    for target in targets:
        assert f'[{target}] SUCCEEDED' in report
sources = list((root / 'CSharp').rglob('*.cs')) + list((game / 'LocalMods/contentclient/CSharp').rglob('*.cs')) + list((game / 'LocalMods/contentserver/CSharp').rglob('*.cs'))
active_client = Path('C:/Users/Admin/AppData/Local/Daedalic Entertainment GmbH/Barotrauma/WorkshopMods/Installed/3831040655')
sources += list((active_client / 'CSharp').rglob('*.cs'))
for folder in ('Tests', 'Tools'):
    sources += [p for p in (root / folder).rglob('*') if p.suffix in ('.cs', '.csproj', '.py', '.ps1') and not any(v in ('bin', 'obj', '__pycache__') for v in p.parts)]
binaries = [out / 'build/client/InternalsAwareAssembly.dll', out / 'build/server/InternalsAwareAssembly.dll',
            root / 'Tests/GMPlatform/bin/Release/net10.0/GMPlatform.dll', root / 'Tests/ContentContracts/bin/Release/net10.0/ContentContracts.dll', root / 'Tests/PhysicsContracts/bin/Release/net10.0/PhysicsContracts.dll']
reports = [out / name for name in ('tests.log', 'content-tests.log', 'physics-tests.log', 'contracts-client.log', 'contracts-server.log', 'audit.log', 'gm-audit.log', 'source-evidence.json', 'event-graph-evidence.json', 'sync-log-tests.log')]
result = {'capturedUtc': datetime.now(timezone.utc).isoformat(), 'status': 'PASS', 'scope': 'offline implementation slice only; full requested Live Editor and SpawnSub are incomplete',
          'tests': tests, 'contentTests': content, 'physicsTests': physics, 'syncLogEvidence': sync_logs, 'nativeContracts': {'client': client, 'server': server},
          'sources': [digest(p) for p in sorted(set(sources))], 'binaries': [digest(p) for p in binaries], 'reports': [digest(p) for p in reports],
          'notRun': ['game and dedicated server', 'Barotrauma physics integration', 'multiplayer edits and late join', 'network latency/loss/reordering', 'visual and GPU acceptance'],
          'notImplemented': ['structural transactions', 'wire topology editor', 'prefab placement and dependency-safe clone/delete in the journal', 'rotation/resize', 'dynamic submarine baseline/SpawnSub', 'portable presets and submarine blueprint export']}
(out / 'verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'status': result['status'], 'sourceFiles': len(result['sources']), 'scope': result['scope']}, ensure_ascii=False))
