# SubSeal: submarine preview confidentiality and transfer policy

## Threat model and scope

The adversary controls their Barotrauma client, can remove contentclient, replace C# assemblies,
read every network packet and every locally stored file, and can deliberately request any
submarine name/hash that the vanilla protocol accepts. A client capability bit is **not** an
attestation of an unmodified client. Server-host/owner is a separately trusted identity.

Only one strong confidentiality property is feasible: **a submarine not authorized for
delivery must never be transmitted to an untrusted connection in plaintext or with a key
that connection receives**. Once an attacker is authorized for the full submarine (e.g.
to participate in a round) they can save the decrypted bytes or reconstruct the
submarine from gameplay state. GUI blocking and deletion of decrypted in-memory XML
cannot prevent this.

## Verified engine data paths (Barotrauma source)

- `FileSender.StartTransfer` creates `FileTransferOut` by reading the entire file;
  `FileSender.Update` sends the resulting `Data` in chunks. The server's existing
  StartTransfer Harmony patch replaces the bytes before that Update.
- `FileSender.ReadFileRequest` accepts requested submarine files, campaign saves,
  and compressed mod files.
- `ModSender.CompressMod` uses `SaveUtil.CompressDirectory(mod.Dir,...)`, so
  **unlisted files** in a package folder may be exposed by a mod download.
- Campaign saves can contain embedded `.sub`. The existing `SaveResealer`
  encrypts `.sub` entries before sending a campaign save to a sealing-capable client.
- `SubmarinePreview.Create` allocates an independent preview frame with Escape and
  Close handlers; the existing client patch only replaces rendering in its preview
  drawing area, rather than painting the entire screen.

Upstream:
- https://github.com/FakeFishGames/Barotrauma/blob/master/Barotrauma/BarotraumaServer/ServerSource/Networking/FileTransfer/FileSender.cs
- https://github.com/FakeFishGames/Barotrauma/blob/master/Barotrauma/BarotraumaServer/ServerSource/Networking/FileTransfer/ModSender.cs
- https://github.com/FakeFishGames/Barotrauma/blob/master/Barotrauma/BarotraumaClient/ClientSource/Map/SubmarinePreview.cs

## Enforcement architecture

1. Keep `contentserver` as the only owner of file-transfer policy; keep `contentclient`
   as optional visual/decoding integration. Do not introduce a second protocol.
2. In sealing mode, only provide session-wrapped content keys according to
   `Entitlement`; preserve existing `.sub`/campaign-save AES-GCM sealing.
3. No client receives plaintext through `AllowUnsealedForIncapableClients` while
   `PreviewProtection` is enabled, even if an operator enables that compatibility switch.
   When preview protection is disabled and compatibility is enabled, enforce entitlement
   on plaintext as well.
4. Check enabled multiplayer-synced mod package trees once at server initialization.
   Allow only archives whose source trees contain no non-public `.sub`, `.save`, or
   recognized nested archives (`.zip`, `.7z`, `.rar`, `.gz`, `.tar`).
   Unknown or uninspectable archives are denied. On each transfer use only a hash set.
   Explicit public `.sub` names are configured using `PublicSubmarines`.
5. Apply that archive restriction in normal sealing and transfer-refusal modes.
   In request-only fallback, deny all mod requests because the file-specific path
   cannot be established before entering the original request reader.
6. Keep `SubmarinePreview` rendering scoped; leave Close/Escape GUI handlers intact.
   Do not patch all `SpriteBatch` draws or other screens.

## Compatibility and explicit limitations

- This is not client integrity verification. A modified client can claim sealing
  capability and receive authorized keys. `Entitlement` must decide when those
  keys are released.
- **The existing entitlement policy grants active and campaign-owned submarines.**
  An authorized participant can therefore extract those submarines. Protecting them
  even during active gameplay requires a different server-to-client data model, not
  another `SubmarinePreview` patch.
- **Vanilla clients without contentclient cannot decrypt sealed submarine files.**
  Refusing them a protected transfer can prevent joining/loading protected rounds.
  A full-function vanilla gameplay client and cryptographically hidden complete
  geometry are incompatible with the current engine representation.
- `GateMode.RefusingRequestsOnly` is not a security boundary: unsolicited campaign
  save transfers can still occur. Server logs a CRITICAL warning. Use a supported
  `FileSender.StartTransfer` patch point and verify runtime mode is `sealing` or
  `refusingalltransfers`.
- The mod scanner protects ordinary files in scanned package directories. It does
  not inspect arbitrary steganographic payloads, unknown custom containers,
  source content modified after scanning, independently published Workshop items,
  preexisting local files, or third-party outbound network protocols. Do not describe
  this as all-path protection without a full content/network audit.
- This repository currently contains
  `Content/Map/BeaconStations/pure_empty_beacon_station.sub` inside **Primary package**.
  With the default `PublicSubmarines=""`, Primary package's automatic mod archive
  download will be refused. Confirm whether this file is safe to publish, and only
  then explicitly mark it public; otherwise separate protected sub files from
  downloadable content. Do **not** whitelist a blueprint just to make downloads work.
- Preview screenshots embedded in a publicly distributed `.sub`, installed
  Workshop package, or other preexisting local file remain readable.

## Design alternatives and trade-offs

- **Server denial/sealing (implemented):** preserves secrecy for files not released,
  but non-capable clients cannot load protected content.
- **Safe preview-only surrogate (not implemented):** server supplies an independently
  generated sanitized `.sub` with no protected geometry. It requires versioned
  metadata, cache/hash isolation, and a later authenticated transition to the true
  game asset. Vanilla clients still receive the true asset when needed for play.
- **Server-side rendering / streaming (not implemented):** full confidentiality would
  need a fundamentally different simulation, rendering, interaction and replication
  architecture, and would not be a C# preview patch.
- **Client GUI-only masking (existing optional presentation):** inexpensive and
  preserves close/navigation when scoped, but never a security boundary.

## Verification checklist

A test must record PASS, FAIL, BLOCKED or NOT_RUN, with captured server logs,
transfer traces, client screenshots and benchmark numbers:

| Scenario | Required outcome |
| --- | --- |
| Protected unselected `.sub`, standard contentclient | No plaintext transfer or unauthorized key; scoped black `ого.` preview |
| Protected unselected `.sub`, no C# / contentclient disabled | No plaintext transfer; test failure to load protected round honestly |
| Modified client, forged capabilities, arbitrary file/hash requests | Never grants key for `Entitlement`-denied submarine |
| Protected `.sub` embedded in mod archive | Transfer refused and logged |
| `.save` or nested archive in downloaded mod | Transfer refused and logged |
| Public mod without protected files | Mod transfer unchanged |
| Public `.sub` explicitly listed in settings | Publication documented and intentional |
| Campaign save containing `.sub` | Embedded file sealed when transfer permitted |
| Lobby, tab menu, selection, campaign UI, Escape, Close | Other GUI works; no fullscreen black overlay |
| Reconnect, revoked lease, multiple clients, campaign switch | No stale keys or unexpected plaintext |
| FileSender unavailable / patch mismatch | CRITICAL diagnostics, no claim of full enforcement |
| 10/20+ simultaneous transfers; large save and mod packages | Measure transfer rate, allocations, main-thread duration and memory |

### Execution status

These changes were made by source inspection using GitHub. **No Barotrauma process,
LuaCs C# build, end-to-end multiplayer test, rendering test, or load benchmark has
been executed in this environment.** Accordingly, the above scenarios are
`NOT_RUN`, not `PASS`. Production use requires game-version-matched compilation
and multiplayer validation.
