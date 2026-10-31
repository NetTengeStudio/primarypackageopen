# Prepared one-way walls: editor serialization and round activation

## Source contracts

- `Structure.Load(ContentXElement, Submarine, IdRemap)` is static and returns a `Structure`.
- `Structure.Save(XElement)` returns the serialized `XElement` and inserts it in its parent XML.
- `ContentXElement.GetAttributeString` reads optional attributes.
- The client editor uses `MapEntity.SelectedList`; `GameMain.SubEditorScreen.Cam` determines cursor world coordinates.

These methods were inspected in `evilfactory/LuaCsForBarotrauma` before creating the integration hooks.

## Architecture

`WallPreparations` stores **configuration** of editor-selected wall instances, while `WallRules` owns only **active physical overrides**. No Farseer gate is attached while a wall is merely prepared. Each prepared wall stores a `pp_ecface` attribute (0..3) directly in its own `Structure` XML element; no external file, mutable global ID list, or editor-only content dependency is required. The attribute is read when that wall is created in the game and is written when the editor saves the submarine.

Editor flow: select exactly one physical wall; invoke `ec_init` (mouse side), `ec_init left/right/up/down` (explicit normal), or `ec_init off` to remove its saved marker. Save the `.sub` file normally.

Round flow: start with no active override. A GM calls `ec_ready`. The server activates every valid prepared wall and broadcasts one full authoritative wall-rule snapshot. The operation is idempotent for the current round. `ec 1/2/off` remains available **after activation**, so the administrator can override individual walls during gameplay; `ec list` remains available before activation.

Removed structures are retired from both configuration and active rule stores. Round end and plugin disposal reset these stores; `Structure.UpdateSections` still rebinds active gates to recreated bodies.

## Security and compatibility

A client cannot authorize an `ec_ready` request without the built-in console-command permission path (both the `DebugConsole.ExecuteClientCommand` gate and `MpPermissionGuard` are used). Server-side map XML remains the source for prepared wall configuration; a client does not transmit arbitrary wall IDs in `ec_ready`.

Wire formats, `contentserver` and `contentclient` are unaffected by this wall activation feature. The wall physics still requires compatible client-side simulation for correct prediction; server-side authoritative contacts alone do **not** guarantee an unmodified client renders identical one-way collision.

## Required tests (NOT_RUN)

1. In sub editor, select a wall, run `ec_init`, save and reopen the submarine. Verify `pp_ecface` persists on the correct `Structure` and not any other.
2. Verify `ec_init off` removes the serialized marker and that cloned/new walls do not inherit another instance's configuration.
3. Without `ec_ready`, walk into prepared walls in both directions; no one-way `PreSolve` gates may be attached.
4. Start a round with marked walls, activate by `ec_ready`; verify one-way contacts, all permitted directions, `ec 1/2/off` overrides, and repeated `ec_ready`.
5. Validate host, dedicated server, late join, server restart and no-client-C# connectivity, including complete snapshots and damaged walls with multiple sections.
6. Remove a wall, end the round, save/reopen/restart, and compare lifecycle state and Farseer callback subscriptions against vanilla.
7. Measure activation CPU time and snapshot size for 0/10/100/1000 prepared walls; verify that idle frames perform no prepared-rule scans.

**All builds, gameplay tests and benchmarks are NOT_RUN.** This draft must not be merged before those checks succeed.
