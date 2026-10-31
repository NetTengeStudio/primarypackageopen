# HullIndex reconciliation coverage

Source-level defect: `HullIndex.Sweep` stepped through `ItemRegistry.Items.Length` (preallocated at 8192), processing 96 slots per call. For a map containing only 50 assigned items, the audit examined them in the first sweep, then spent approximately 85 subsequent calls traversing unused capacity. A direct `Item.currentHull` mutation from an unobserved mod or engine path could therefore remain unindexed for up to ~85 sweeps before a full cycle returned to the assigned slot.

`ItemRegistry.SlotLimit` now exposes the actual high-water mark of assigned slots in the current registry generation. `HullIndex.Sweep` traverses only `[0, SlotLimit)`, wrapping the cursor at the real limit and retaining the same 96-inspections-per-tick budget. This keeps the slower safety audit independent from normal event-driven `StoreHull` updates, while reducing error persistence in small and medium maps.

**Work model:** O(min(96, assignedSlots)) per sweep; complete coverage after ceiling(assignedSlots / 96) sweeps, compared with ceiling(arrayCapacity / 96) sweeps previously. The high-water mark can exceed live count before compaction; the algorithm does not assume dense live items.

Required tests: static and dynamic entities with 1, 50, 96, 97, 1000, 8192 assigned slots; deliberate off-hook `currentHull` changes; removal/reassignment/compaction; `FireFeature.Verify` mismatches before and after the correction. **Build and runtime validation NOT_RUN.**
