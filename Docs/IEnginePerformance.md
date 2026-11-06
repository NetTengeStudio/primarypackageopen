# IEngine: duration lookup and application hooks

## Changes

`durations` replaces the two repeated `DurationList.Find` operations in
`StatusEffect.Apply` with a view grouped by the effect's parent. Queries examine
only that parent's entries, in their original order. Targets are read from the
live entries on every query; they are deliberately not used as cached keys.
Single-target matching retains reference identity, while list matching retains
`SequenceEqual` equality and order. Duration reset rules remain in the game's
original method.

The index owns reusable arrays and a dictionary. An observed append updates them
incrementally. Every other source-list version change rebuilds the view, including
insertions, replacements, reordering, removals and external additions. Count and
version deltas are not treated as proof of an append. Reset releases retained
effect/target references at `StatusEffect.StopAll`, mode changes and uninstall.
The index runs on the simulation thread, like the original unsynchronized list.

The single-target method no longer allocates its compiler-generated closure on
every invocation. With optimization disabled or an observed predicate, the
fallback constructs and invokes the game's actual predicate. Foreign caller
prefixes/postfixes remain in the call path; foreign transpilers and observed
search predicates disable indexed lookup. Unexpected instruction contracts leave
the original instructions intact and are reported.

`effecthooks` specializes the item and character `statusEffect.apply.*` calls in
the protected `StatusEffect.Apply` body. Event names and immutable lookup keys
are cached for the round. An event with a subscriber goes through the normal
dispatcher. A positive bucket hint can only choose normal dispatch; its live
dispatcher remains the authority and never runs a removed callback from a hint.
`EventService.Reset` and `Dispose` invalidate the hints. A missing subscription
is checked again while holding LuaCs's writer
gate, which serializes subscription changes, reset and disposal. Contention goes
through normal dispatch and retains its waiting behavior. Empty events return
without building the argument array or boxing the time/position values.

Callbacks still receive a fresh argument array: pooling or reusing that array
would be incorrect because a callback may retain it. Custom hook implementations,
disposed services, disabled optimization and observed dispatcher methods retain
normal dispatch. No simulation frequencies, distances or gameplay rules change.

Both features are included in the existing `mode="on"` configuration. Custom
feature lists must explicitly include `durations` and `effecthooks`.

## Measurements

Measurements use BenchmarkDotNet 0.14.0, its default job and MemoryDiagnoser on
.NET 8.0.26 x64. The benchmarks link the production computation modules, load the
installed client assemblies and compare both approaches in the same run.
Initialization, key creation and the first index build occur before measurement.
The hook baseline has the existing IEngine `luabus` optimization enabled.

| Workload | Before | After | Speedup | Allocation per query, before → after |
| --- | ---: | ---: | ---: | ---: |
| Duration lookup, 32 entries, 32 parents | 39.64 ns | 12.81 ns | 3.09× | 64 B → 0 B |
| Duration lookup, 4,096 entries, 128 parents | 3,403.2 ns | 243.0 ns | 14.01× | 64 B → 0 B |
| Duration lookup, 4,096 entries, one parent | 74.54 µs | 33.93 µs | 2.20× | 64 B → 0 B |
| Repeated application hook, no subscriber | 131.51 ns | 50.44 ns | 2.61× | 304 B → 0 B |
| Repeated application hook, one subscriber | 204.82 ns | 193.56 ns | 1.06× | 384 B → 192 B |

These are individual operation measurements, not gameplay FPS. Rebuilding the
duration index costs O(N); a stable query costs O(1 + K), where K is the number of
entries belonging to that parent. The one-parent case remains O(N). Growing
buffers and initial key creation allocate outside the warmed steady state.
The hook cases reuse one event identifier, so the last-key cache and positive
subscription hint are warm. They do not establish the same ratio for a workload
that continuously changes identifiers or subscriptions.

The existing scene logs in `IEngine/out` describe a different run, with 60
durations in one scene and the item update/MapEntity work dominating. They do not
establish that a particular saved game will obtain a large total frame-time
improvement from these two changes. Current gameplay frame time and FPS have not
been measured.

## Verification

`Tools/verify_iengine.ps1` compiles the complete current client and server sources
with the .NET 8 C# compiler, then compiles/runs offline integration executables.
It writes response files, compile logs and regression logs to the selected output
directory. Its MonoGame publicized reference is generated in that directory;
installed libraries are not modified. It does not start a game or server.

Verified cases include:

- 2,000 seeded source-list and target-list mutations, including insertion,
  reordering, replacement, external append, removal, clear and live target edits;
- first-match ordering, reference equality versus value equality, empty targets
  and preservation of the original exception for an encountered null entry;
- incremental append through the production wrapper and index reset;
- real patched single/list `StatusEffect.Apply` calls, reset duration, disabled
  mode, reference verification, foreign predicate patches and caller observers;
- zero allocated bytes across 512 warmed pairs of actual patched single/list
  duration reapplications;
- absent, added, removed and replaced Lua subscriptions, true/false/null results,
  argument identity, callbacks retaining arrays, custom hook implementations,
  disposed services and a writer installing a subscription during contention;
- simultaneous installation with the existing item-target and interval
  specializations, plus rejection of a changed hook instruction contract.

Client tests execute against installed `Barotrauma.dll` 1.13.5.0, MVID
`53d1dc7e-9565-4faa-bdce-cea10ef771ef`. The installed root `DedicatedServer.dll`
has no LuaCs `EventService`, while the publicized server compile reference does.
Server offline tests therefore explicitly use `Publicized/DedicatedServer.dll`
1.13.5.0, MVID `94340cc3-bb99-4d8b-bd39-f8404b4701b1`. They are reference-assembly
execution tests, not verification of a running dedicated server. The existing
`intervals` feature rejected this server reference's hash in that verification
run. The subsequent [condition compiler change](IEngineCompiler.md) replaces
that hash gate with a complete semantic IL contract; current client and server
offline checks both activate `intervals`.

Example invocation from the mod root:

```powershell
.\Tools\verify_iengine.ps1 `
    -OutputDirectory "$env:TEMP\PrimaryPackage-IEngine-check" `
    -ServerDirectory 'C:\Steam\steamapps\common\Barotrauma\Publicized' `
    -ServerRuntimeDirectory 'C:\Steam\steamapps\workshop\content\602960\2559634234\Binary'
```

Raw evidence and the task-scoped benchmark project are retained under:

```text
C:\Users\Admin\.codex\visualizations\2026\11\03\01a19fc2-f7fd-7510-9d49-df7af488245b\iengine-performance
```

The accepted duration reports are in `benchmarks/measure-large` and
`benchmarks/measure-rest`; accepted hook reports are in `benchmarks/measure-hook-v4`.
Exploratory hook runs, including a measured callback slowdown corrected before
the accepted run, are retained separately and
are not treated as successful measurements. Compile/regression evidence is in
`regressions-final`. The source scan checklist is in `scan-checklist.txt`; allocation and
LINQ matches in the installation/transpilation/reference paths were reviewed
separately from warmed simulation queries.

For manual gameplay verification, inspect `ie_report` for `durations` and
`effecthooks`. `ie_verify on` compares duration lookup with its original predicate;
turn verification off before using `ie_bench 4 20 off/on` to measure the complete
engine on the same running scene. No gameplay or multiplayer validation was run
during this change.
