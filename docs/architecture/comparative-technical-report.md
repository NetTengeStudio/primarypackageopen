# Primary Package — reconstruction audit and comparative technical report
Date: 2026-10-31  
Branch: `architecture/wall-collision-lifecycle-20261031`  
Baseline: `77f9ed95cf434a5deb87a269b5314f87a82ef50c` (`main`)  

This is a **partial architectural reconstruction**, not the complete implementation requested in the original specification. No Barotrauma runtime measurements or compilation were executed in this environment. All improvements below are **algorithmic work-model claims**, not measured speedups.

## Existing execution architecture (observed)

1. `Core.ModuleHost` installs modules and Harmony hooks, distributes `GameSession.StartRound`, and releases them on host shutdown.
2. `IEngine.EngineHost` installs feature-specific Harmony patches through `Runtime`, with opt-in features, observers, instrumentation, and an A/B benchmark command.
3. `GameScreen.Update(double)` runs physics body iteration and calls `MapEntity.UpdateAll`. `ExecutorFeature` can substitute `MapEntityExecutor.Run` and its item, hull, gap, power and status phases.
4. `ItemRegistry` stores item slots, `Dormancy` sleeping bitsets, `SpatialCore` cells and union-find wiring islands; `WakeHooks` processes observed mutations.
5. `BodiesFeature` transcompiles the two physics-body enumerations, using `BodyIndex.ForUpdate` candidates.
6. `WallCollisionPlugin` hooks wall section reconstruction and maintains Farseer `PreSolve` one-way filtering. `WallCollisionServer` and `WallCollisionClient` exchange rule changes and snapshots via `ModNet`.
7. `ColdPlugin` advances `ColdField` and applies temperature effects. `ColdServerNet` sends quantized room temperatures; clients interpolate with `ColdClientNet`.
8. `contentclient` and `contentserver` have their own C# roots and run configuration; this branch has **not** modified either protection module.

Source verification involved the repository plus matching upstream methods in `evilfactory/LuaCsForBarotrauma`: `GameScreen.Update`, `PhysicsBody.Remove/RemoveAll`, `Structure.Remove/ShallowRemove/UpdateSections`, `Connection.ConnectWire/DisconnectWire`, `Wire.RemoveConnection`, and `Submarine.Loaded`.

## Before / after: work models

| Execution path | Before | After | Invariants / risk |
| --- | --- | --- | --- |
| Wall rules idle frame | O(R) stale-rule sweep per session tick | O(1) usual idle path; O(1) keyed removal plus O(B) body detach on wall removal | Engine removal hooks may differ in local build; must test collisions |
| Farseer PreSolve listener | Periodic check whether to detach | Detach on last removal and round teardown | Hook lifecycle needs host/client validation |
| Wall network revision | Snapshot accepted but stale previous epoch could survive; any newer delta accepted | Snapshot establishes epoch; requires contiguous delta sequence | Wire format unchanged; test missed/late/reordered messages |
| Spatial region rasterization | O((V+N)(S+1)A) grid-candidate checks every tick | O(V+N+S) dependency check on unchanged inputs; original rasterization on changes | Fast-moving viewers will still trigger full work |
| Spatial topology | Union-find only grows; removal / disconnection could preserve obsolete islands | Dirty topology rebuilt once after edge deletions; adds retain union path | Rare topology churn may cause O(I+W) spikes; benchmark |
| Dynamic submarines | Submarine list snapshot only refreshed at rebuild | O(S) identity check and rebuild on changes | Coordinate domains remain consistent |
| Event-wake queue | Older expiry could refreeze an item; moved-item queue path ignored new deadline table | Superseded entries rejected; all writers update deadlines | Verify slot reuse, wake deadlines, and timer behavior |
| Physics body membership | O(B) scan on each `ForUpdate`, including two game loops | O(D) changed candidates at enumeration boundary, plus periodic O(P+B) audit | Delayed mutations visible next enumeration; safety under mid-loop removes |
| Cold network candidate discovery | O(H) room scan at every net interval | O(Dq) quantized-dirty candidates on normal sends; O(H) on full/keepalive | Extra quantization per field write; total performance unknown |

Legend: `R` active wall rules, `B` cached bodies, `P` all game physics bodies, `D` pending mutations, `V` viewers, `N` NPCs, `S` loaded submarines, `A` cells intersecting each observation disc, `I` indexed items, `W` signal-wire edges, `H` hulls, `Dq` distinct quantized temperature changes. Hash lookups are *average-case* O(1).

## Functional and performance verification

| Verification stage | State | Evidence |
| --- | --- | --- |
| GitHub changes and compare | PASS | Branch commits persisted; diff against baseline available |
| Relevant upstream API inspection | PASS | Functions identified in the LuaCs/Barotrauma source checkout |
| Client C# assembly build | NOT_RUN | No local Barotrauma/LuaCs toolchain access |
| Dedicated server C# assembly build | NOT_RUN | No local toolchain access |
| Gameplay / Farseer / characters / ragdolls | NOT_RUN | Game not executed |
| Mod interoperability incl. `contentclient` / `contentserver` | NOT_RUN | No integrated live session |
| Client disconnect / reconnect / late join | NOT_RUN | Network simulator/game not executed |
| Functional regression corpus | NOT_RUN | Integration tests not executed |
| CPU, GC, P95/P99, FPS, packets, latency | NOT_MEASURED | No baseline or post-change runtime profile |

No numerical CPU, network throughput, memory or frame-time speedups are asserted.

## High-priority acceptance sequence

1. Build `Shared`, `Client`, `Server` through the installed LuaCs compiler. Fail the branch on API or Harmony mismatches.
2. Compare `ec 1`, `ec 2` directions and `ec off` against baseline for idle, moving, ragdoll, wall hole creation and section rebuilds; ensure pre-solve callbacks are detached after map/round removal.
3. Compare spatial islands against fresh full reference rebuild after signal-wire disconnect, inventory move, item deletion, submarine spawn/despawn and spectator movement. Verify no dangerous sleep of active life support.
4. Run physics-body update enumeration with create/remove/enable transitions; verify candidate coverage and no iterator invalidation.
5. Stress cold state with 10, 100 and 1000 hulls and changing gaps. Compare raw quantized network state, full/keepalive packets and late-join sync. Record `pp_cold` `InspectedHulls` values.
6. Run `ie_verify on` and `ie_bench` interleaved scenarios for client, host and dedicated server under consistent seeds/scene/players; evaluate whole-frame P95/P99 and CPU, not just microbenchmarks.
7. Test connecting without client-side C# and ensure submarine asset restrictions work server-side. The current branch cannot establish this property.

## Remaining major reconstruction work

- Audit and redesign the remaining expensive pathways in `IEngine.Execution`, including item scheduling, event indexing, status-effect targeting, priority paths and compatibility with foreign Harmony patches.
- Rebuild and stress the `contentclient`/`contentserver` protection handshake without trusting client-side obscuration.
- Confirm or implement `ec_init` and `ec_ready` according to their editor/server lifecycle requirements; neither command was present in the inspected `WallCollisionCommand`.
- Independently validate `ColdField` physics, thermal sources and multiplayer synchronization; the work in this branch only changes network *candidate discovery*, not the thermal solver.
- Add reproducible baseline and post-change profiles before declaring a measurable win or merging.

**Decision:** retain this PR in draft until assembly, functional and performance acceptance passes. Large additional rewrites without an executable baseline risk regressions and are not justified solely by predicted complexity.

## 2026-10-31 continuation: transfer-lease ownership and staged wall activation

**contentserver:** Introduced per-connection lease ownership, exact-file pending indices and an armed-retry candidate set. `Outstanding` and `FindPending` now use O(1) average dictionary lookups instead of global O(L) scans, revocation visits only that connection's K leases, and retries use O(A+T) scheduling on A armed leases and T active transfers rather than scanning all L leases and up to T transfers for each retry. Wire protocol, cryptographic formats, grant ACKs, permissions and default fail-closed settings are unchanged. See `subseal-lease-index.md`.

**Wall geometry:** Explicit world-to-Farseer simulation coordinate conversion now occurs at `WallGeometry.TryFaceFromWorldPoint`. Previously `Character.WorldPosition` and `Body.Position` were compared as though measured in the same coordinate system, potentially selecting the wrong one-way face.

**Prepared wall lifecycle:** Added `WallPreparations` and editor `ec_init` with per-structure `pp_ecface` XML metadata. `ec_ready` performs an explicit server-authoritative activation and one network snapshot. Unlike the previous branch, `ec 1/2/off` commands now require prior `ec_ready` in that round; this is an intentional behavior change to satisfy the requested inactive-until-ready contract. See `wall-collision-preparation.md`.

**Unverified integration risks:** Harmony `Structure.Load`/`Save` postfixes, selected-wall editor command path, the dedicated-server round lifecycle, and the transfer-lease state transitions require builds and live tests. No measured performance improvement or actual gameplay test is available, so the PR remains draft.
