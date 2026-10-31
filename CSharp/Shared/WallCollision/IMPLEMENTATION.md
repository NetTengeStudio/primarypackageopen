# WallCollision: editor preparation and explicit activation

## Source-verified integration points

- `BarotraumaClient/ClientSource/Screens/SubEditorScreen.cs`: editor selection uses `MapEntity.SelectedList`.
- `BarotraumaShared/SharedSource/Map/Structure.cs`: `SpecialTag` is serialized with `SerializableProperty.SerializeProperties` by `Structure.Save`; `UpdateSections` can rebuild wall bodies.
- `Libraries/Farseer Physics Engine 3.5/Dynamics/Contacts/Contact.cs`: a `PreSolve` callback can set `Contact.Enabled = false` for the current step; this is not a permanent fixture change.
- `Libraries/Farseer Physics Engine 3.5/Dynamics/ContactManager.cs`: contact processing can run on parallel workers.
- `Libraries/Farseer Physics Engine 3.5/Dynamics/Fixture.cs`: `GetAABB(out AABB, childIndex)` exposes bounds for each shape child.
- `BarotraumaShared/SharedSource/Characters/Character.cs`: `SimPosition` is in simulation units, `WorldPosition` in display units.

## Architecture and alternatives

Chosen: retain the existing `WallCollision` module and `ModNet` protocol; mark only editor-selected `Structure` instances with a versioned `SpecialTag` suffix; activate them after an authorized `ec_ready`; use normal Farseer `PreSolve` contact disabling for one-way movement. This does not change fixture collision masks, object positions, velocities, or unrelated walls.

Rejected: global collision masks (affect unrelated contacts), moving characters artificially (not true physics), permanent `Contact.Enabled` (resets each step), writing a separate per-submarine XML manifest (can desynchronize from map edits).

A crossing is recorded for a dynamic body entering from the permitted half-space while moving through the wall, and remains passable across adjacent wall sections. Each prepared wall holds its own small thread-safe crossing map; only active crossings are swept at 10 Hz. Destroyed/replaced wall bodies are detached; `ec off` and round resets remove gates and transient crossing states.

## Commands

1. In the submarine editor select exactly one physical structure; run `ec_init` (defaults to mode 2), `ec_init 2 left|right|up|down`, or `ec_init 1`. Run `ec_init off` to remove the persisted marker. **Save the submarine**.
2. Start a round with the saved submarine. Prior to activation the original physics remains in force.
3. A permitted GM runs `ec_ready` once to activate prepared walls and broadcast a snapshot. Server console can also activate.
4. A permitted GM points at a prepared wall and runs `ec 1`, `ec 2 [direction]`, or `ec off`. Changes are runtime-only, synced to clients; `ec off` preserves the original prepared marker so another round can reuse it.

The two packet channel IDs end in `V2` to avoid misreading the changed `uint32` revisions by older clients. The protocol sends an initial snapshot (ready flag, revision, wall IDs, modes, faces), delta changes, and on-demand resynchronization. Clients without the matching Primary Package version do not implement the new physics, and cannot be expected to predict it correctly.

## Validation matrix (not run)

- Editor: select 0, 1, or many walls; unsupported structure; prepare/unprepare; save/reopen; verify other `SpecialTag` content unchanged; edit angle/size.
- Activation: no prepared walls, one and many prepared walls; before and after `ec_ready`; repeat `ec_ready`; `ec off` restores original fixture response.
- Geometry: ±X, ±Y, rotated walls; each direction; thin/thick/multi-section and damaged walls; reverse mid-crossing; standing contact; different collision shapes; ragdolls; large and very fast items; world-edge contacts.
- Multiplayer: GM and non-GM; unauthenticated client request; packet loss, lost delta, late join, reconnect, level change, ID remap and wall removal; dedicated server and listen server.
- Performance: benchmark 0/10/100 active gates and 0/10/100 concurrent crossings, pre-solve cost, Gen0 allocation count, frame time, network byte count.
- Engine compilers: actual LuaCs client and server build, dedicated-server load, `ModuleHost` attach/cleanup.

**Status: source-inspected implementation only.** No LuaCs/Barotrauma binary, game environment, controlled physics harness or benchmark has been run here. Therefore no claim of verified compilation, zero tunneling at all speeds, ragdoll correctness or zero regression is made. Farseer contact callbacks cannot prevent every possible tunneling case when continuous collision detection fails to create a contact in the first place. Absolute synchronization of locally simulated physics cannot be guaranteed against an incompatible or modified client; the server's simulation remains authoritative.
