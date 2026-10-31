# Wall-collision lifecycle reconstruction

## Engine grounding

Reference: `evilfactory/LuaCsForBarotrauma`, `Barotrauma/BarotraumaShared/SharedSource/Map/Structure.cs` (master, inspected 2026-10-31).

- `Structure.UpdateSections()` removes previous Farseer bodies and creates replacement bodies. The existing `UpdateSections` postfix must therefore reattach collision gates to the new bodies.
- `Structure.Remove()` and `Structure.ShallowRemove()` are both implemented and remove the structure's physics bodies. Their prefixes are used to retire the exact corresponding rule **before** removing bodies.
- `GameSession.EndRound()` provides cleanup of the rules and the Farseer contact callback.
- The client's `GameSession.Update()` hook remains solely for snapshot retry scheduling. The server no longer needs this hook for collision-rule maintenance.

## Work model

Before: each game-session update traversed every active wall rule for stale entities (`O(R)` per tick, `R` = rule count). Even with no removed entities, `R` references were inspected each tick, plus a contact-sink release check.

After: structure removal performs a single dictionary lookup (`O(1)` average) and releases only the affected wall's Farseer bodies (`O(B)`, `B` = that wall's gated body count). Rebinding is performed on `UpdateSections`, when the engine actually recreates wall bodies. The contact callback detaches on the last removed gate, not on a global frame hook. The client retains one constant-time sync timer check per session update.

`WallContacts.OnPreSolve` now verifies that a gated fixture actually belongs to the wall collision category, not merely that another fixture on its body has that category.

## Invariants and compatibility

- `ec 1`, `ec 2`, `ec off`, rule packet formats and snapshot-revision mechanics are untouched.
- Removal checks the structure object identity as well as its numeric ID to avoid accidentally retiring a rule belonging to another instance.
- Round cleanup clears every registered gate and unregisters the Farseer `PreSolve` delegate.
- This change does **not** alter one-way penetration logic or solve potential character-controller vs. ragdoll differences.

## Verification status

- PASS: source-level inspection of the matching upstream lifecycle methods.
- PASS: changes committed to an isolated branch, without modifying `main`.
- NOT_RUN: Client/Server/Shared build with LuaCs, game startup, physics, networking, replay, profiling and benchmarking.
- NOT_MEASURED: FPS, CPU, GC and network improvements. The reduction in steady-state rule scanning is analytical, **not** an observed benchmark result.

## Required follow-up tests

1. In singleplayer, activate `ec 1` and `ec 2`, change wall damage to force `UpdateSections`, verify character and ragdoll contacts from both sides.
2. Remove a selected wall and confirm that no gate remains; create a different wall that reuses a numeric ID and check isolation.
3. End the round with active rules and start another round; verify that the old world has no `PreSolve` subscription.
4. Test host, dedicated server, late joining clients and reconnects, verifying full snapshots and subsequent rule changes.
5. Record CPU profiles with 0/10/100/1000 active rules for the old and new branches before asserting a speedup.
