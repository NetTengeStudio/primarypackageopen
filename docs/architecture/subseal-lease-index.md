# Server-side submarine transfer lease indexing

## Scope and ground truth

Inspected `contentserver/CSharp/Server/Leases.cs`, `TransferPatches.cs`, `LeaseChannel.cs`, `SessionKeys.cs`, `Entitlement.cs`, `Commands.cs`. Existing transfer gating, server-side permission checks, encryption, and client wire protocol remain unchanged.

## Existing work model

The original server had one global `Dictionary<uint, Lease>`. `Outstanding(connection)`, `FindPending(connection,type,path)`, and `Revoke(connection)` scanned *all* live leases across all connections. A pending transfer invokes these methods on a game-network callback, so the request cost grew with unrelated connected clients.

## Replacement

Maintain a three-way ownership model:

- `byId`: authoritative registry of all leases, retained for ACK lookup, sweeps, diagnostics and re-grants.
- `byConnection[connection].Owned`: the exact set of lease IDs owned by a network connection, including recently consumed leases (retained until expiry so their keys can be securely wiped).
- `byConnection[connection].Pending[(fileType,exactPath)]`: the single active, unconsumed lease for a given connection and transfer. An integer `Outstanding` is updated on issue, consumption and revocation.

All transitions (`Issue`, `MarkConsumed`, `Revoke`, `Sweep`, `RevokeAll`) maintain the indices together. A consumed lease no longer blocks a new transfer of the same file. Exact ordinal path comparison is preserved, as in the existing `FindPending` loop. The key material is zeroed on expiry/revocation as before.

The ID allocator skips the special global revoke ID and verifies that an ID is not already live before allocation, including after uint wraparound.

The handshake, license entitlement, transfer policy and sealing algorithm are unchanged; this is a *state ownership and index reconstruction*.

## Complexity

| Operation | Previous | After |
| --- | --- | --- |
| Check outstanding leases | O(L) | O(1) average |
| Find pending transfer | O(L) | O(1) average |
| ACK by lease ID | O(1) average | O(1) average |
| Revoke one connection | O(L) scan, O(K) removals | O(K) |
| Expiry sweep | O(L) | O(L) |
| Revoke all | O(L) | O(L) |

`L` is total live leases across clients and `K` is lease count for the one connection. Additional memory is O(L). The dictionary's hash table adds overhead; measure under representative connection counts before claiming a total CPU reduction.

## Required tests (NOT_RUN)

- Create 16 leases for one connection; 17th refused; a second connection has independent capacity.
- Re-request an existing pending transfer; lookup returns exactly the same lease until consumed.
- Arm by correct connection succeeds; ACK by unrelated connection or already consumed lease fails.
- Consume once, verify outstanding count decreases once, a new transfer of the same path can be issued, and both leases remain visible for cleanup.
- Expire issued and armed leases at their respective retention thresholds; verify all indices and secure erasure.
- Revoke one client with several pending and consumed leases; all its entries disappear, other clients remain untouched.
- Wrap `nextId` through 0 and `Protocol.AllLeases` with a colliding live ID; verify uniqueness.
- Integrate in Barotrauma host and dedicated server with 1/8/64 clients, concurrently awaiting transfers and reconnecting.
- Compare CPU work on repeated submarine download requests, active transfer throttling, GC and total network bandwidth with the baseline.

**Verification:** GitHub source review only; no build, gameplay execution or benchmark has been run. This change does not establish that a user who receives an authorized playable submarine can never extract its contents.

## Retry scheduler reconstruction

The original `TransferPatches.RetryArmed` iterated all `L` live leases, then iterated all `T` active outgoing transfers **for each eligible lease** to count transfers for that recipient.

`Leases` now tracks an exact `armedIds` set, updated on ACK, consumption, revocation and expiry. Each sweep snapshots the `A` armed IDs to avoid mutating an enumerated collection while `StartTransfer` callbacks consume them. The current outgoing transfers are counted once by connection (`O(T)`). The scheduler checks each armed lease in `O(1)` average and updates counts by the actual synchronous change in `FileSender.ActiveTransfers.Count` after each retry.

**Work model:** `O(L + A*T)` worst if all leases are eligible previously; now `O(A+T)` typical. `A` is armed and unconsumed leases. The per-recipient and global transfer limits are unchanged.

The synchronous `StartTransfer` mutation of `ActiveTransfers` was verified against `Barotrauma/BarotraumaServer/ServerSource/Networking/FileTransfer/FileSender.cs` in `evilfactory/LuaCsForBarotrauma`.

Additional tests: interleave successful and failed retry attempts, verify recipient totals after every successful transfer, test mid-loop ACK/consumption and disconnect; compare scheduled retry order without assuming dictionary enumeration order, and validate under the game's native 16/5 transfer limits.

## Security-critical transfer failure handling (2026-10-31)

The upstream `FileSender.StartTransfer` constructs `FileTransferOut` with **plaintext bytes** and adds it to `ActiveTransfers` before the Harmony postfix. Its `Update` method removes transfers whose `Status == FileTransferStatus.Error` **before** calling `Send`.

Previously `AfterStartTransfer` called the cryptographic transformation without a catch/finally. If sealing threw, the Harmony postfix could exit with a plaintext active transfer. It also retained the original plaintext array after successful replacement.

The postfix now catches crypto and reflection exceptions, marks the transfer `Error` before any future `Send`, and zeroes the original transfer plaintext buffer in a `finally` block after both success and failure. Replacement must produce a separate non-null buffer. The original sealed ciphertext buffer is not scrubbed.

The `AllowUnsealedForIncapableClients` compatibility switch no longer permits plaintext fallback when `PreviewProtection` is enabled, even if explicitly configured. When `PreviewProtection=false`, the existing opt-in downgrade remains available.

**Known limit:** `FileSender.OnStarted` is invoked by upstream before the Harmony postfix. This change prevents the standard sender's subsequent `Update` from sending plaintext after a failure; it does not prove that arbitrary third-party callbacks cannot observe the in-memory payload at startup. Live tests are required to validate exact hook ordering and subscriptions.

**Required tests:** inject exceptions from `SaveResealer.Reseal`, `TransferSeal.Seal` and `EngineSymbols.OverwriteData`; confirm status `Error`, plaintext erased, no outgoing unsealed file; repeat under slow network and multiple clients. Benchmark the added O(file-bytes) plaintext erasure as a deliberate security cost. **NOT_RUN**.

## Transactional session-key replacement

Before reconstruction, `SessionKeys.Establish` called `Drop(connection)` **before validating** the new ECDH public key. An invalid Hello with a correct contract hash therefore destroyed an established session without negotiating a replacement, potentially invalidating an active transfer's handshake state.

The ECDH derivation now occurs first; only after successful key derivation is the old session securely wiped and the new one published. The parser also checks that the SubjectPublicKeyInfo input was fully consumed, and the temporary raw ECDH secret is cleared in `finally`, including on key-derivation failure. `LeaseChannel` continues to revoke existing leases after a successfully replaced session, as before.

Test: valid session followed by invalid point, malformed/trailing SPKI and unsupported curve must leave the original key/session unchanged. A successful second Hello must install a different key and wipe the prior key. Repeat with live leases and deferred transfers. **NOT_RUN**.
