# Physics-body active-set maintenance

## Source analysis

`GameScreen.Update(double)` in `evilfactory/LuaCsForBarotrauma/Barotrauma/BarotraumaShared/SharedSource/Screens/GameScreen.cs` traverses `PhysicsBody.List` twice each frame: first to update active/character colliders and later to record previous transforms of enabled dynamic bodies. `BodiesFeature` replaces both list references with `BodyIndex.ForUpdate()`.

Before: `BodyIndex.ForUpdate()` scanned **every candidate** for stale membership on both traversals each frame, even when no body changed state. A full source-list audit for missing bodies already ran every 120 calls.

## Algorithmic redesign

- `PhysicsBody.Enabled`, `PhysicsBody.UserData`, Farseer `Body.BodyType`, constructor and removal hooks are already installed in `BodiesFeature`; preserve them.
- `BodyIndex.Touch` and `Forget` queue all additions and removals into a reference-deduplicated mutation set. `ForUpdate` applies this set **at the next enumeration boundary**, never directly from an engine callback. This prevents the callbacks from structurally modifying the list while `GameScreen.Update` is iterating it. The removed body is excluded on the next traversal.
- Event-delivered changes are deduplicated by reference in one `HashSet` and evaluated against the latest body state (so a disabled→enabled transition before the next enumeration does not incorrectly remove an eligible body). Both constructors and removals are queued; there is no mutation of the active candidates list while another caller is enumerating it.
- The periodic full scan and coverage audit is retained as a bounded recovery path when another mod modifies unobserved fields or bypasses setters. It runs once per 120 `ForUpdate` invocations, not on every enumeration. Repaired items are applied at the audit boundary.
- The existing `GameScreen.Update` predicates are untouched, so a conservatively retained ineligible body is still filtered by the original game code until the audit runs.

## Work reduction

Steady frame: membership maintenance changes from `O(B)` per `ForUpdate` to `O(D)` changed bodies, often `O(1)`, while `B` body enumeration and physics updates themselves remain necessary. Every 120 calls, `O(B+P)` audit work still executes (`P`=total registered physics bodies).

This claim is about *index maintenance*, not total physics time. No FPS/CPU improvement has been measured.

## Required tests

1. Toggle `Enabled` or `BodyType` on hundreds of physics bodies and compare candidates against a fresh full scan, before and after a 120-call audit. Trigger a constructor or `Remove()` during the first `GameScreen` foreach; verify that the returned list is not structurally mutated before the next `ForUpdate`.
2. Create, remove, and re-enable bodies during round transitions. Ensure no stale/duplicate candidates remain.
3. Verify that a body changing state twice before the next `ForUpdate` is not dropped.
4. Run client/server simulations with ragdolls, disabled character colliders and mods that manipulate Farseer objects.
5. Compare `IEngine bodies` diagnostics and measured CPU time in identical scenarios.

Verification: source-level API paths inspected; **Client/Server builds, gameplay tests and benchmarks NOT_RUN**.
