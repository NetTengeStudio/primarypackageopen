# Spatial execution: topology and region invalidation

Verified source inputs:
- `CSharp/Shared/IEngine/Simulation/SpatialCore.cs`, `WakeHooks.cs` and `ItemRegistry.cs` in Primary Package.
- `Barotrauma/BarotraumaShared/SharedSource/Items/Components/Signal/Connection.cs` and `Wire.cs` from `evilfactory/LuaCsForBarotrauma`.

## Root causes

1. The spatial dependency graph uses union-find, which supports *unions only*. Before this change, reconnecting or removing an item, disconnecting a signal wire, or moving an item between inventories could change graph topology without invalidating the connected component. This can leave unrelated items in the same activation island and corrupt spatial sleep/wake decisions.
2. `ItemRegistry.OnUnlisted` freed the item slot but did not notify the spatial graph. The spatial index could retain a stale slot without forcing a rebuild, even though `ItemRegistry`'s live count remained consistent.
3. `ComputeRegions()` rasterized all player/NPC/submarine influence discs **every execution tick**, regardless of whether any of their defining inputs changed. The index also stored a snapshot of `Submarine.Loaded` only during full rebuilds, so dynamic submarine loading could invalidate cell-coordinate identities without any index notification.
4. An expired event-wake queue entry was applied even when a later event had extended that slot's wake deadline. Queue entries were not invalidated after item-slot remapping.

## New execution model

- **Signal-wire additions:** union the two indexed endpoints for non-power wires.
- **Signal-wire deletions, parent-inventory changes and item removal:** mark the dependency topology dirty; rebuild once at the next spatial tick. Rebuild is required for split operations because union-find has no inverse for edge removal. Unchanged topology and ordinary movements still use the incremental indexing path.
- **Submarine topology:** compare the loaded-submarine object identities and ordering against the indexing snapshot at the beginning of each spatial tick. Rebuild once when the coordinate domains change, before incremental reindexing or region queries. Avoid a new round-trip through all indexed items on stable scenes.
- **Observer regions:** store exact previous observer coordinates, radii, loaded submarine identities and positions. If every dependency is unchanged, skip disc rasterization and cell-diff processing entirely. Otherwise recompute exactly: no positional threshold or approximation.
- **Event wakeups:** keep the latest deadline per slot and discard superseded entries. Rebuilding the item registry invalidates stale queued deadlines, preventing their accidental application to reused slots.
- **Power wiring:** connection events do not add power-only edges to the spatial graph; this matches the existing full-index rebuild, which excludes `Connection.IsPower`.

## Complexity

- No region-affecting change: previously `O((V+N)*(S+1)*A)` cell-raster work per tick, now `O(V+N+S)` dependency comparison (`V`=viewers, `N`=NPCs, `S`=loaded submarines, `A`=candidate grid cells per influence disc). The unavoidable snapshot of active observers still occurs.
- Removal/disconnect/reparent: topology rebuild `O(I+W)` in indexed items and wire graph size; multiple changes before the next spatial tick coalesce into one rebuild.
- Ordinary movement with unchanged topology: retains the existing pending-item incremental processing.
- Loaded-submarine changes: `O(S)` identity comparison per spatial tick, and one full rebuild only when the submarine set or ordering changes.
- Event deadline checks: amortized `O(1)` per expired queue entry.

## Correctness and regression requirements

- Create an island of signal-wired items, unlink the only bridge, move one group beyond viewing range; confirm each component can sleep and wake independently.
- Take an item into and out of character inventory; confirm no old attachment survives.
- Repeatedly remove and respawn items while `activeset` and `spatial` are enabled; compare indexed count and membership to a fresh full rebuild.
- Extend an event wakeup before the first deadline expires; confirm the earlier queue entry does not refreeze the item.
- Verify power wires do not merge islands.
- Compare region sets, pinned islands, frozen/awake membership against full recompute after static viewers, moving viewers, submarine motion and radius changes. Spawn/despawn a shuttle in the middle of the round to validate `Submarine.Loaded` invalidation.
- Run multiplayer client/server tests including late join, round restart, item deletion and inventory transfers.

## Test status

- Source API check: inspected LuaCs/Barotrauma `Connection.DisconnectWire`, `ConnectWire`, `Wire.RemoveConnection(Connection)`, and Primary Package lifecycle paths.
- Build: **NOT_RUN** (requires user's LuaCs/Barotrauma environment).
- Runtime regression and network tests: **NOT_RUN**.
- CPU/FPS/GC comparison: **NOT_MEASURED**. The complexity reductions above are analytical, not measured speedups.

Known constraint: a topology split currently causes a full rebuild, not dynamic graph deletion. This trades rare-event rebuilding for correct connected-component membership; it should be profiled under mass item removal.
