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

## Transactional multipart snapshot completion (31 Oct 2026)

Previous client logic set `synced=true` on the **first nonempty full packet**, even if the server needed multiple packets of up to 220 hulls. Subsequent request retries stopped immediately, so a failed later packet could leave part of the state uninitialized indefinitely. Normal `ModNet` traffic uses reliable delivery, but serialization/send failures can still produce a partial attempt.

The full response now consists of ordered reliable data chunks followed by a zero-entry `FlagFull | FlagSnapshotComplete` completion marker. The server **does not send** that marker if any chunk fails to queue. The client marks the full snapshot received only on this explicit marker; deltas remain unchanged. Wire item records (Hull ID and quantized temperature) are unchanged. This modifies only a reserved flags bit and adds one small completion packet to each full response.

**Compatibility note:** an older server without the marker will not satisfy the newer client's synchronization criterion, so it will keep requesting snapshots. Both packages should be deployed together; protocol version negotiation and multi-epoch snapshot handling remain future integration requirements. The marker is not a checksum of all hull IDs or a transaction barrier against concurrent configuration changes; do not claim complete network correctness until dedicated tests pass.

**Required tests (NOT_RUN):** 0, 1, 219, 220, 221 and 1000 hull snapshots; simulate failed chunk queue before final marker, validate client retries; on reconnect or round start ensure no stale marker accepts a different snapshot. Test with packet lag/out-of-order transports and verify reliable ordering. Measure extra one-packet cost only for snapshot requests.

### Backward-compatible full-state coverage and graph invalidation

The completion marker alone would leave an updated client requesting snapshots indefinitely when connected to a legacy server that sends only `FlagFull` chunks. `ColdClientNet` therefore accumulates a set of **distinct, successfully resolved** hull IDs from full-state chunks and marks synchronization only when every locally indexed hull has been covered. A zero-count marker still confirms the end of the sender's request but cannot set `synced=true` if expected hulls are missing. This keeps old-server compatibility without reverting to the old 'first full chunk is complete' bug.

The coverage set is reset whenever `ColdField.Version` changes; `ColdClientNet.Update` now checks graph identity even when already synchronized, and requests a new full state if local hull topology changes. Subsequent normal deltas continue to apply without affecting coverage. Under reliable ordered delivery, the new server terminator follows all chunks. A missing or unresolved hull keeps the client in retry mode instead of silently accepting a partial snapshot.

**Limitations:** full fragments do not carry an explicit epoch/transaction ID, so overlap of full responses from separate requests is not cryptographically prevented. A future protocol revision should add epoch, total count and snapshot checksum if out-of-order/multi-generation delivery must be handled formally. Test against legacy and updated servers, graph replacement with the same hull count, and interleaved replay before merge. **NOT_RUN**.
