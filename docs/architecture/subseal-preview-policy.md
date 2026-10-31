# SubSeal client preview policy: cached skeleton lock correctness

The previous `PreviewLock.IsLocked` returned early through `Decide` whenever `elementLoaded == true`, which meant that the later `SubmarineSkeleton.IsReduced` and `leases.TryGet` checks were **unreachable** for any loaded skeleton. A previously downloaded sealed submarine with a loaded reduced skeleton, but no valid lease key, could therefore be treated as unlocked; the UI could fail to display the expected black preview and text even though its underlying content could not be rehydrated.

`PreviewDecision.ShouldLock` is a pure state evaluator. When protection is active, missing, corrupted and unreadable files are locked. Sealed files without a loaded element are locked. If the loaded element has been reduced to metadata-only XML, the preview stays locked until its lease key is present. Already rehydrated full XML retains existing behavior, as a client that legitimately decrypted it already possesses the geometry. Unsealed local files remain unaffected by this preview policy, subject to the separate server transfer gate.

`PreviewLock.IsLocked` now passes the actual file state, a reduced-element flag and key ownership into that evaluator. It probes the seal once and only looks up the lease ID for sealed loaded reduced XML.

The production decision source is compiled in `Tests/SubSealCore` together with existing server encryption and lease tests. Added policy-table cases cover loaded/unloaded sealed files, missing/corrupt/unreadable cases, keys present/absent, full XML, unsealed files and disabled policy. **Tests NOT_RUN** until a compiler runner is available.

**Limit:** this fixes the incorrect lock predicate; it does not establish visual correctness of the GUI overlay, editor preview, or compatibility without client C#. Integration requires host/dedicated gameplay tests, including an unauthorized late join and the original all-black-screen exit regression.
