# Change-propagated cold replication

## Baseline source execution

`ColdField.Diffuse` already traverses every active hull at the configured simulation interval, and `Nudge`/`Set` are its other temperature write paths. The previous `ColdServerNet.Broadcast` then traversed all `H` hulls at every network interval to quantize and compare with the last transmitted bytes, even when temperatures were unchanged at wire resolution.

## Refactored ownership

- `ColdField` is now the owner of **quantized temperature change detection**. Each server-only write to `temperature[i]` updates an 8-bit shadow quantization and enqueues `i` at most once before its next replication pass.
- `ColdServerNet` owns the last **transmitted** quantized value `sent[i]`. It examines dirty candidates, filters any values that returned to the old transmitted byte, and batches only actual updates.
- When the topology/index ordering changes (`ColdField.Version`) or the quantization scale changes, the server performs its original full scan. A configured keep-alive also forces a full scan and transmission at its existing interval.
- The candidate set is consumed after collecting the outgoing payload. A late-joining client's targeted full snapshot does not consume broadcast changes.
- Changes are compiled only for `SERVER`, with the client retaining its existing interpolation, networking and presentation paths.
- `InspectedHulls` counts the actual number of candidate hull indices examined by the network stage, so production diagnostics can contrast full scans with dirty-only passes.

## Algorithmic effect

Before: replication preparation costs `O(H)` per network pass, irrespective of whether the wire-visible temperature changed.

After: replication preparation is `O(D)` for `D` distinct hull indices crossing a quantization boundary since the previous pass. Simulation remains `O(H+E)` per diffusion step. Full snapshots/keep-alives remain `O(H)` and are not optimized away. Additional memory is `O(H)` for shadow bytes, dirty flags and candidate slots.

Each temperature write performs an O(1) quantization check. Actual gain depends on the ratio of temperature writes to network intervals and the fraction of hulls changing wire-visible state; **no runtime speedup is claimed without profiling**.

## Correctness test matrix

1. Stable temperatures for many intervals: dirty count stays zero and no periodic full scan occurs until keep-alive.
2. Quantized temperature changes in one room: only that room is considered; repeated nudges coalesce.
3. Multiple writes that eventually restore the old sent byte: no redundant packet emitted.
4. New hulls or reordered hull indices: `Version` forces a full update, no stale index sent.
5. Gap topology changes without a hull count change: graph version forces full update.
6. Live `ColdConfig.MinTemperature`/`MaxTemperature` change: full quantization revalidation.
7. Run client/server with late join, reconnect and lossy delivery; compare quantized snapshots and timed keep-alives against baseline.
8. Compare `InspectedHulls`, bytes sent, GC, P95/P99 network preparation and whole-frame CPU across 0/10/100/1000 hull scenarios.

Status: source-path analysis and GitHub changes only. `Client/Server` compilation, gameplay tests, and measured comparisons **NOT_RUN**.
