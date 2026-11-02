"""Compare native client/server event-error snapshots without launching the engine.

Counts before level generation and the later ID table are separate snapshots; combining
them would hide the first divergence behind later allocations. Original logs are read-only.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re

ENTITY = re.compile(r'-\s+(\d+):\s+(.*)')
COUNT = re.compile(r'Entity count (before|after) generating level: (\d+)')


def read_snapshot(path):
    return parse_snapshot(path.read_bytes(), path)


def parse_snapshot(raw, path):
    lines = raw.decode('utf-8-sig', errors='replace').splitlines()
    sections = {'before': {}, 'current': {}}
    counts = {}
    section = None
    for number, line in enumerate(lines, 1):
        count = COUNT.fullmatch(line)
        if count:
            counts[count[1]] = {'line': number, 'count': int(count[2])}
            section = None
        elif line == 'Entities:':
            section = 'before'
        elif line == 'Entity IDs:':
            section = 'current'
        elif section is not None:
            entity = ENTITY.fullmatch(line)
            if entity:
                identity = int(entity[1])
                if identity in sections[section]:
                    raise ValueError(f'Duplicate native ID {identity} in {path}:{number}')
                sections[section][identity] = {'line': number, 'description': entity[2].strip()}
            elif line and not line.isspace():
                section = None
    if 'before' not in counts or len(sections['before']) != counts['before']['count'] or not sections['current']:
        raise ValueError(f'Incomplete native diagnostic snapshots: {path}')
    return {'path': str(path.resolve()), 'sha256': hashlib.sha256(raw).hexdigest(),
            'error': lines[0] if lines else '', 'counts': counts, 'sections': sections}


def compare(client_path, server_path):
    return compare_snapshots(read_snapshot(client_path), read_snapshot(server_path))


def compare_snapshots(client, server):
    sections = {}
    for name in ('before', 'current'):
        c, s = client['sections'][name], server['sections'][name]
        differences = [{'id': identity, 'client': c.get(identity), 'server': s.get(identity)}
                       for identity in sorted(c.keys() | s.keys()) if c.get(identity, {}).get('description') != s.get(identity, {}).get('description')]
        sections[name] = {'clientEntries': len(c), 'serverEntries': len(s), 'differenceCount': len(differences), 'firstDifferences': differences[:32]}
    for snapshot in (client, server):
        del snapshot['sections']
    return {'client': client, 'server': server, 'sections': sections,
            'scope': 'Native diagnostic snapshots; does not identify the origin of differing map data or prove SpawnSub involvement'}


def main():
    root = Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--logs', type=Path, default=root / 'ServerLogs')
    parser.add_argument('--output', type=Path, default=root / 'Docs/PrimaryPackage/LiveEditor/event-graph-evidence.json')
    args = parser.parse_args()
    pairs = []
    for client in sorted(args.logs.glob('event_error_log_client_*.log')):
        server = client.with_name(client.name.replace('_client_', '_server_', 1))
        if server.is_file():
            pairs.append(compare(client, server))
    status = 'FINDING' if any(section['differenceCount'] for pair in pairs for section in pair['sections'].values()) else 'PASS' if pairs else 'NOT_RUN'
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({'status': status, 'pairs': pairs}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'status': status, 'pairedReports': len(pairs),
                      'preGenerationDifferences': [p['sections']['before']['differenceCount'] for p in pairs],
                      'currentIdDifferences': [p['sections']['current']['differenceCount'] for p in pairs]}))


if __name__ == '__main__':
    main()
