# Consistency invariants: wall revision epochs and spatial wakeups

## Root causes

**Wall snapshots.** `WallCollisionClient.OnRoundStart()` resets `synced` but not the static `WallCollisionNet.Revision`. Previously the snapshot path accepted a new revision **only** if it was serial-number newer than that from the previous host. When reconnecting to a different server with a smaller revision, the snapshot was applied but `Revision` stayed stale, so subsequent deltas were discarded.

**Wall delta gaps.** The client accepted any newer revision without checking for contiguous ordering. A lost/interleaved update could therefore leave the wall rules inconsistent while `synced == true`.

**Spatial wake deadlines.** The earlier change introduced per-slot `eventDeadline` guards, but `SpatialCore.ProcessPending()` still appended direct queue entries without updating `eventDeadline`. Those events would be silently ignored when they expired.

## Changes

- Unconditionally establish the revision epoch from a full authoritative snapshot whenever the client is not already synced to a newer snapshot.
- On an incremental `Set` packet require `revision == (ushort)(lastRevision + 1)`; otherwise reset synchronization and request a new full snapshot via existing backoff. Handle wraparound naturally.
- Ignore unsupported packet kinds rather than interpreting them as updates.
- Route new/moved item wake deadlines through `OnEventWake`, the single writer of the queue and the matching per-slot expiration state.

## Non-goals

This does not add cryptographic integrity or change the client/server protocol. The changes preserve existing message formats and host ownership. `contentclient` and `contentserver` are unmodified.

## Required verification

1. Receive snapshot revision 60000; start a new session with snapshot revision 12, then accept delta 13.
2. Snapshot revision 65535 followed by delta revision 0.
3. Snapshot revision 11 followed by delta 13: client must re-request snapshot, not accept delta 13.
4. Duplicate or stale delta must be ignored; invalid kind must not change state.
5. Simulate item reassignment and event wake, confirm `OnEventWake` records matching expiry and refreezes only after its newest deadline.
6. Real multiplayer: late join, reconnect, server restart, dedicated vs host, wall destruction and reload.

Status: inspected source control flow; **build, integration tests, gameplay and measured performance NOT_RUN**.
