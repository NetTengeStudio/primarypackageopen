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
