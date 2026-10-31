# IEngine SpatialCore wake deadline scheduler

Source verified: `SpatialCore.ProcessPending` calls `OnEventWake(slot)` on reindex, and `Dormancy.WakeSlot` can also schedule a wake. The previous `Queue<(Slot,Until)>` appended a new entry on every wake, even when it was the same slot. An independently maintained deadline array rejected stale entries on expiration, but **did not prevent queue growth**. In continuously changing scenes this produced O(wake calls within 5 seconds) queued tuples rather than O(distinct slots).

Replaced with `IndexedDeadlineQueue`: an intrusive binary min-heap with a slot-to-position array. `Set` updates the active entry for the slot in O(log N), `Remove` deletes one slot on entity unlisting in O(log N), and `TryTakeExpired` pops only due entries in O(log N). Heap nodes are ordered by deadline, then slot ID for determinism. The maximum pending count is O(active distinct slots), regardless of how often the same item wakes. The engine's 5-second event grace, `Dormancy.TrySleep` policy, and region-based freeze checks are retained.

Each `Start`, `Stop`, and topology rebuild clears the heap. Newly freed slots are removed explicitly. A persistent queue instance and resizable arrays avoid allocation during ordinary wake updates. Diagnostics now include `wakePending` for observing bounded behavior.

**Testing:** `Tests/IEngineCore` directly compiles the production queue and checks repeat-wake boundedness (100,000 updates to one slot), stable expiration ordering, both increasing and decreasing deadlines, removal/reuse, slot growth and 50,000 randomized operations against a dictionary reference model. `.github/workflows/iengine-core.yml` runs them on .NET 8. These checks remain NOT_RUN until a runner executes them.

**Additional required gameplay checks:** damaged ships, moving/disconnected wiring, wall fixtures, item lifecycle compaction, camera movement, inactive items becoming active and simulation time jumps. Compare P95/P99 tick time, allocations and pending deadline counts under high-frequency movement before claiming an actual performance gain.
