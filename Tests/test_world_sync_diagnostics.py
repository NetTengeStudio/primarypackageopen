import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('world_sync', Path(__file__).resolve().parents[1] / 'Tools/diagnose_world_sync.py')
world_sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(world_sync)


class DiagnosticSnapshots(unittest.TestCase):
    def parse(self, text, name='native.log'):
        return world_sync.parse_snapshot(text.encode('utf-8'), Path(name))

    def test_snapshots_keep_their_own_id_namespace(self):
        result = self.parse('MISSING_ENTITY\nEntity count before generating level: 1\nEntities:\n-   1: Marker\n'
                            'Entity count after generating level: 2\nEntity IDs:\n-   1: Replacement\n-   2: Level\n')
        self.assertEqual(result['sections']['before'][1]['description'], 'Marker')
        self.assertEqual(result['sections']['current'][1]['description'], 'Replacement')
        self.assertEqual(result['sections']['before'][1]['line'], 4)

    def test_first_divergence_is_not_hidden_by_later_entity_reuse(self):
        suffix = 'Entity count after generating level: 1\nEntity IDs:\n-   1: Replacement\n'
        client = self.parse('MISSING_ENTITY\nEntity count before generating level: 1\nEntities:\n-   1: Marker\n' + suffix, 'client.log')
        server = self.parse('Missing item\nEntity count before generating level: 1\nEntities:\n-   1: Level\n' + suffix, 'server.log')
        comparison = world_sync.compare_snapshots(client, server)
        self.assertEqual(comparison['sections']['before']['differenceCount'], 1)
        self.assertEqual(comparison['sections']['current']['differenceCount'], 0)
        self.assertEqual(comparison['sections']['before']['firstDifferences'][0]['id'], 1)

    def test_duplicate_id_in_one_snapshot_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'Duplicate native ID'):
            self.parse('MISSING_ENTITY\nEntity count before generating level: 2\nEntities:\n-   1: First\n-   1: Second\n')

    def test_truncated_snapshot_is_not_treated_as_a_match(self):
        with self.assertRaisesRegex(ValueError, 'Incomplete native diagnostic'):
            self.parse('MISSING_ENTITY\nEntity count before generating level: 2\nEntities:\n-   1: First\n')


if __name__ == '__main__':
    unittest.main()
