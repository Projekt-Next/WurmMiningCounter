# Architecture

This document is the primary technical source of truth for WurmMiningCounter 1.8.4.

## Pipeline

```text
Wurm Event logs
  -> EventLogReader
  -> EventPipeline (fixed-rate 250 ms polling and checkpoints)
  -> FastEventDispatcher
     |- MiningEventLane (bounded FIFO per profile)
     |  -> MiningEventProcessor -> MiningSessionCounter / TotalCount
     `- EventLineCoordinator (bounded Ordered Context FIFO per profile)
        |- VeinContextProcessor
        |- NearbyScanProcessor
        `- EventAlertModule
             -> EventAlertDelivery (bounded FIFO worker)
                |- ExternalSoundService -> Sounds/*.wav
                `- EventAlertPopupHost (one active popup)
  -> MainViewModel -> MainWindow

Wurm _Skills + console.<profile>.log
  -> LiveSkillMonitor (250 ms polling)
  -> SkillStateTracker
  -> LiveSkillProfileSnapshot
     |- MainViewModel / MainWindow
     `- MiningStatisticsSessionBaselineStore
        `- SkillStatisticsViewModel / SkillStatisticsWindow
```

`App` is the composition root: it creates services, connects the Event Pipeline and the single application-lifetime `LiveSkillMonitor`, and manages startup and shutdown.

`LiveSkillMonitor` is the only production `_Skills` path. Its shared snapshot feeds MainWindow and automatic Skill Statistics; there is no separate Live Mining monitor or manual Statistics runtime.

`EventPipeline` owns source discovery, bounded incremental reads, continuity,
source checkpoints, and fixed-rate polling. It commits an output checkpoint
only after `FastEventDispatcher` returns `Complete`. The dispatcher performs
nonblocking handoff to independent bounded per-profile Mining and Ordered
Context lanes; workers do not block source polling or one another. An
indeterminate handoff faults that source and is never automatically replayed.

Event Alerts observe normalized Event lines on the ordered Context lane. The
runtime catalog supplies per-trigger configuration; matching alerts enter one
bounded delivery worker. Sound playback and popup admission are isolated from
the Context worker. PopupHost shows at most one active popup and retains at
most two pending notifications. `Sounds` is scanned for top-level WAV files
when the Alerts editor opens; fresh built-in triggers have no default sound
assignment.

Full rotation continuity for monthly/daily overlap, cross-file ordering, and
late old-tail after the grace period remains **DEFERRED**. The Event Pipeline
does not deduplicate text or merge active segments across files.

## Components

| Component | Responsibility |
| --- | --- |
| `App` | Composition root, startup, shutdown, settings, tray, and pipeline composition. |
| `LogDiscoverySnapshotService` | Publishes the shared, serialized, versioned snapshot used at startup and by Settings > Find locations; builds fail-closed desired/current reconciliation evidence. |
| `WurmLogLocationLocator` | Discovers configured direct and Steam Wurm data roots, profile logs, and recognized files; reports partial results and physical-identity uncertainty. |
| `PlayerProfileIdentityResolver` | Resolves stable `PlayerProfileId` from confirmed physical directory identity, with guarded path-scoped fallback that cannot silently reactivate across a lifetime gap. |
| `EventPipeline` | Source discovery, bounded Event reads, continuity, per-source checkpoints, terminal source status, and fixed-rate 250 ms polling. |
| `EventLogReader` | Incremental byte reads, strict UTF-8, partial lines, BOM, fingerprints, and a startup snapshot safe at UTF-8 boundaries. |
| `FastEventDispatcher` | Nonblocking handoff to module lanes; records terminal status per module/profile/generation. |
| `MiningEventLane` | Bounded FIFO and worker per profile for Mining output, continuity, reset, and profile release. |
| `EventLineCoordinator` | Ordered Context lane: bounded FIFO and worker per profile for batches, Prospecting/Vein, Nearby, markers, reset, continuity, and release. |
| `EventAlertModule` | Ordered observer; removes the standard Event-log timestamp through the shared normalizer, then applies ordinal exact matching for built-ins and case-insensitive whole-word/contiguous-phrase matching for custom triggers. |
| `EventAlertCatalog` | Thread-safe runtime snapshot of built-in and custom trigger definitions. |
| `EventAlertConfigurationController` | Validates Add/Edit/toggle/Delete changes, persists settings, and publishes successful changes to the catalog. |
| `EventAlertSettingsNormalizer` | Restores canonical built-in identity/text, retains allowed delivery options, and filters invalid custom triggers and sound names. |
| `EventAlertDelivery` | Bounded FIFO admission and one worker for fault-isolated sound and popup delivery. |
| `EventAlertSoundCatalog` | Discovers safe, top-level `.wav` names from the application `Sounds` directory on editor refresh. |
| `ExternalSoundService` | Resolves a selected WAV filename within `Sounds` and isolates playback failures. |
| `EventAlertPopupHost` | Dispatcher-owned popup lifecycle with one active window and bounded pending notifications. |
| `AlertsWindow` | Trigger table and in-window Add/Edit panel; persists per-trigger Enabled, Sound, Popup, and selected filename settings. |
| `IOrderedEventConsumer` | Ordered-pipeline consumer contract: `Process` and idempotent per-profile `Invalidate`. |
| `EventLogReconciliationScanner` | Scans configured candidate profiles and their direct `logs` directories for the Event pipeline. |
| `EventLogFileNameMatcher` | The sole Event-log filename rule. |
| `MiningEventParser` | Stateless parser for 12 confirmed Mining messages. |
| `MiningSessionCounter` | Thread-safe cumulative Total and current per-profile material counters. |
| `MiningEventProcessor` | Routes a recognized event to the counter for its profile. |
| `LiveSkillMonitor` | The sole application-lifetime `_Skills` path: profile/source discovery, startup boundaries, Mining Current bootstrap, live polling, continuity, and immutable per-profile snapshot publication. |
| `SkillStateTracker` | Thread-safe per-profile Current, Last Gain, Session Gain, and raw `PositiveUpdateCount` for numeric skill updates; bootstrap sets Current only. |
| `MiningStatisticsSessionBaselineStore` | Thread-safe, application-lifetime, in-memory per-profile baseline for Mining ticks, gain, and session start. |
| `SkillStatisticsViewModel` / `SkillStatisticsWindow` | Automatic multi-profile Skill Statistics; Mining adds gain/hour, milestone/ETA, and session-relative derived statistics. The window owns no monitor and performs no source I/O. |
| `VeinContextProcessor` | Owns real per-profile `ActiveVeinContext`: progressive Material observations from Prospecting, emergence, and Examine; guarded Exact decrement; contradiction-safe bound-attempt invalidation; immutable context change notifications; and per-profile stale-Nearby invalidation after Ceiling start or synchronized direct Prospecting. It owns ordered-tail augment eligibility for known-Quality PlainRock and finalized normal Direct Salt results; it also retains the last PlainRock result, including MaterialOnly, per profile solely as transient presentation state. |
| `NearbyScanProcessor` | Ordered per-profile consumer for `There is ... nearby.`; retains the latest independent Nearby Scan snapshot and its separate ordered-tail Salt/Flint eligibility, and clears both as required by continuity/reset boundaries or confirmed cave-context transitions. |
| `MainViewModel` | Presents Mining counters, read-only Vein Context/Nearby Scan, and Live Skill snapshots; subscribes to domain events and marshals updates to the UI dispatcher. |
| `TrayIconService` | `NotifyIcon` using `Resources/WurmMiningCounter.ico`, tray menu, Settings, AlertsWindow, one `SkillStatisticsWindow`, and Dispatcher. |
| `SettingsService` | Safe JSON loading and atomic saving; `Save()` returns success/failure, and runtime commits settings only after success. |
| `SettingsViewModel` | UI settings without infrastructure dependencies. |
| `RuntimeDiagnosticService` | Thread-safe, bounded, never-throw in-memory sink for runtime diagnostics. |
| `DiagnosticsExportSanitizer` | Sanitizes data on Copy and Save Log before it leaves the Diagnostics UI. |
| `WindowPlacementService` | Validates and restores window position and width relative to the available screens. |
| `SingleInstanceService` | Mutex and named pipe for activating the primary instance with `ACTIVATE`. |

## Runtime diagnostics

`RuntimeDiagnosticService` is an in-memory, thread-safe, never-throw runtime
diagnostic sink. It retains at most 1000 entries and truncates a single message
to 8 KiB. Every entry contains timestamp, level (`Info`, `Warning`, or
`Error`), source, and message. `DiagnosticsEnabled` is persisted in
`settings.json`, defaults to `true`, and disables new reports without affecting
Mining, Prospecting, or Event Log processing.

Lifecycle, infrastructure, and recognized domain events are reported, but the
service does not log every raw `EventLogLine` or every Mining yield. Vein
Context updates use the stable domain format:

```text
Profile | VeinContextUpdate | Material=... | Quality=... | Remaining=... | Confidence=...
```

Unknown values are written explicitly.

Diagnostics privacy rule: Runtime Diagnostics may retain detailed local
information in memory for troubleshooting.

`DiagnosticsWindow` may display full diagnostic entries locally. Copy Selected,
Ctrl+C, Copy All, and Save Log all pass through `DiagnosticsExportSanitizer`:
profiles are pseudonymized, local paths are redacted, and `_Event.YYYY-MM.txt`
and `_Event.YYYY-MM-DD.txt` segment names remain visible for correlation.

While open, `DiagnosticsWindow` refreshes only every 500 ms, presents
chronological entries, preserves
multi-selection, supports Copy Selected, Ctrl+C, Copy All, horizontal
auto-scroll, confirmed Clear, and Save Log. Its table has only Time, Level,
Source, and Message columns; Message fills remaining width, does not wrap, and
rows have subtle horizontal separators without vertical grid lines. Save Log is
user-initiated only and writes
`WurmMiningCounter-Diagnostics-YYYY-MM-DD-HHmm.txt` as:

```text
timestamp | level | source | message
```

The shared Clear confirmation removes only in-memory entries; previously saved
TXT files are unaffected.

## Live location discovery and source lifecycle

At startup, `App` performs discovery through one `LogDiscoverySnapshotService`.
The same service is used by Settings > Find locations; each publication has a
monotonic version, and App serializes reconciliation so an older publication
cannot roll back a newer one. Default rules cover Wurm Online Standard
(`%UserProfile%\wurm\players`), Wurm Online Steam (App ID 1179680, its
`gamedata\players` path), and Wurm Unlimited Steam (App ID 366220, its
`WurmLauncher\PlayerFiles\players` path). Steam roots come from the existing
Steam installation/library discovery. Additional direct Wurm data locations
can be configured in Settings. Discovery enumerates configured locations; it
does not search arbitrary disks or determine whether a game process is running.

The locator catalogs recognized Event and Skills files separately from runtime
activation. The existing identity registry maps confirmed physical directory
identity (Windows file ID) to a stable `PlayerProfileId`; where that evidence is
unavailable, identity is explicitly unconfirmed and path-scoped. `ProfileName`
is not the runtime identity key. Directory aliases can resolve to the same
physical source, replacement at the same path is distinguishable when file
identity is available, and same-named profiles at distinct locations remain
separate. When physical evidence is unavailable or claims conflict, Discovery
may show files but cannot use a path/name substitute as retirement evidence;
runtime ownership still has to be acquired through `TryRegisterSource` and its
owner lease.

At startup, `App` seeds the snapshot's candidates into both `EventPipeline`
and the single application-lifetime `LiveSkillMonitor`. Manual Find locations
keeps the LD-3E1 add-only behavior: newly found candidates are offered to both
monitors, while existing sources are not restarted. A complete, current
snapshot with unambiguous physical keys may also produce versioned suppression
intents. Existing Event candidates remain add-only during the session.
Scans with ownership ambiguity or missing or conflicting physical identity
retain prior desired states and retirement intents without executing them; a
later authoritative scan can confirm removal. Incomplete scans are add-only
and cannot prove absence; `IsCurrent=false`
by itself is not a release instruction. Event applies conflicting ownership
evidence at a cycle boundary and suspends reads and admission for affected
files and log directories, retaining existing owners and checkpoints. A read
already in progress may finish before that boundary. A complete, unambiguous
snapshot with physical identity can resume an existing file for its confirmed
original owner. When a different owner is confirmed, Event suspends the affected
directory for the rest of the session; it does not transfer checkpoints or admit
new files there. After restart, unambiguous discovery registers the owner and
establishes an EOF baseline, so lines written before that baseline are not
replayed. A later incomplete snapshot without new conflict
evidence does not undo an original-owner resolution; new conflict evidence
suspends the affected source again. Confirmed removal can use the existing
controlled retirement path. Event
checks the logical and physical logs directories against discovery's File ID at
activation and source-open boundaries. A lost binding suspends the source.
After resolution, Event admits new conflicted-directory segments found by its
normal scan when the authoritative snapshot confirms that directory's owner,
existing states have reached that owner, and registration and log-directory
identity checks succeed. A new conflict or uncertain snapshot blocks admission
until ownership is confirmed again. A previously observed different owner keeps
new files suspended until restart.
Conflict history keeps directory-level guards for later admission and replay
protection. Per-file conflict entries
covered by a retained directory are redundant and discarded; resolved segment
entries are released on retirement or normal expiry; expiry retains evidence
for a suspended, pending, or faulted source.
Disappearance controls are accepted or retried only while the profile owner
and logical-to-physical logs binding remain verified; otherwise the checkpoint
and context remain unchanged.
An unavailable Event logs binding marks its owner source suspended in identity
health, without a terminal fault. Verification is retried on the existing
verification interval or upon new confirmed discovery evidence; recovery checks
the owner and logs identities before resuming the retained checkpoint.
The old profile's admitted FIFO work and other sources remain independent.
These checks do not eliminate a concurrent junction retarget between the check
and file opening; the guard permits writes to reparse-point metadata.
Event retirement is applied at an
EventPipeline cycle boundary, suppresses re-admission, removes all states for
the physical directory, and starts one dispatcher release only after the last
Event state for that `PlayerProfileId` is gone. Other profiles continue polling
while the existing Mining/Context FIFO release drains. Reappearance waits for
successful drain, obtains fresh runtime ownership, and establishes an EOF
baseline rather than replaying old Event history. Failed/canceled release stays
suppressed. Skills retirement similarly releases only the matching Skills
owner; its tracker and Statistics snapshot are retained. Rebinding the same
profile ID requires a fresh valid console `Writing to` declaration for the
Skills file, not a historical declaration from before retirement.

Controlled retirement is driven only by the serialized, versioned complete-snapshot
reconciliation path. Controlled Event retirement removes active source states
and checkpoints for the retired physical directory, drains admitted work
through both module FIFOs, and releases ownership after the last Event state
for a profile is gone. Reappearance follows successful drain and creates a
fresh EOF baseline; it does not reactivate the retired checkpoint or replay
history. Retained Skills Statistics are preserved separately from active
monitoring.

Monitoring Health reflects source lifecycle rather than the mere presence of
retained data: unavailable/identity-blocked states are distinct from active
monitoring, and terminal source/release faults report Monitoring error. Recovery
updates are guarded against stale callbacks and do not clear a current terminal
error; profile source failures remain isolated from other profiles. Normal
shutdown detaches reconciliation and health callbacks before monitor disposal.

## Event-log monitoring

Event candidates come from the configured roots described above. Within each
players root, profiles are derived from the `players\<profile>\logs\` directory
structure.

Only the following are supported:

- `_Event.YYYY-MM.txt`
- `_Event.YYYY-MM-DD.txt`

The local segment-open diagnostic intentionally retains the real profile while
avoiding the local path:

```text
<profile> | EventLog | Segment=_Event.YYYY-MM.txt
<profile> | EventLog | Segment=_Event.YYYY-MM-DD.txt
```

Copy and Save Log pseudonymize `<profile>` but retain the segment filename.

Event Pipeline retains active candidates per profile only when their calendar
segment intersects `[localNow - 5 min, localNow]`. Daily and monthly segments
may be active concurrently. A preceding segment is retained only during that
five-minute grace period; afterwards its source state is released. Historical
segments receive no state and do not participate in reconciliation or reads.
Full rotation continuity and deduplication for overlapping monthly/daily and
late old-tail remain **DEFERRED** in the Event Pipeline source-continuity layer.

Production evidence from the real monthly `_Event.2026-09.txt` stream confirms
that a single monthly segment can cover multiple days and continue through
midnight without daily rotation in that client configuration. It also contains
legitimate repeated identical Event lines in the same second. Consequently,
`timestamp + exact text` is not a safe cross-file deduplication key. The sample
does not include a corresponding daily segment, so monthly/daily duplication
remains **DATA-NEEDED**. Cross-file ordering remains **REACHABLE, IMPACT NOT
CONFIRMED**; late old-tail after five minutes remains the intentional active
window policy, with real Wurm old-tail evidence still **DATA-NEEDED**.

At startup, each existing active Event source receives an EOF baseline; its
historical complete content is not published. A source first admitted later is
baselined according to its creation time and profile ownership continuity, so
replacement/reappearance does not replay uncertain history. If a baseline
cannot be captured, it remains pending and the monitor retries through the
normal source cycle rather than guessing an offset.

The pipeline polls known sources nominally every 250 ms and scans known profile
directories for Event files every 1 second. It does not rerun full location or
Steam discovery on each polling cycle. A separate periodic ownership
verification protects source registrations; it is not a substitute for the
one-second known-log scan.

Live measurement on 2026-08-23 observed Wurm writing Event logs in batches,
usually about every 10 seconds, with one observed batch arriving after about
20 seconds. Wurm-to-file delay can therefore dominate the application's polling
interval.

`EventLogReader` reads from byte offsets with `FileShare.ReadWrite` and supports strict UTF-8, a BOM only at offset zero, CRLF, partial lines, and retries for incomplete UTF-8 and I/O errors. Its checkpoint fingerprint hashes a suffix of up to 4096 bytes before the offset. EventPipeline also verifies sparse continuity signatures: file creation time plus hashes of fixed-size prefix and middle windows, avoiding a full historical-file hash on each verification. These signatures supplement rather than replace the checkpoint fingerprint.

Physical file-line order is authoritative; the `[HH:mm:ss]` timestamp does not establish order. Production EventPipelineV2 hands each output to `FastEventDispatcher`; it commits the source checkpoint only after handoff returns `Complete`. The dispatcher admits work into independent bounded per-profile Mining and Context queues, so processing order is local to each lane and there is no shared atomic ordering across modules. Continuity-loss controls use the same handoff contract and precede later data in each accepted lane; they do not create Mining events or change Mining counters/`TotalCount`.

`RuntimeDiagnosticService` reports startup/source scan, file-state creation,
I/O, access-denied, retry-exhaustion, and terminal source failures. An error
for one file does not stop monitoring other files. `Debug.WriteLine` is used
only for developer diagnostics and is not a Runtime Diagnostics channel.

Reliability coverage includes access-denied and I/O errors while reading logs,
Event-log enumeration errors, persistent read retries, isolation of startup
and subsequent shutdown-step failures, and tray initialization fallback. The
scanner test seam exists only to trigger an enumeration failure
deterministically; the runtime path enumerates directories and files in the
known profile roots.

On confirmed truncation or replacement, the reader does not replay current contents; the monitor establishes a new EOF baseline and propagates `StreamContinuityLost` through the ordered control path. Consumers invalidate dependent Prospecting/Vein state, pending MOI, and Nearby Scan according to their existing semantics. Monthly/daily overlap, cross-file ordering, cross-file deduplication, and late old-tail after the grace period are not currently monitor continuity boundaries and remain **OPEN / DEFERRED**.

There is no currently confirmed Event-log continuity issue requiring
implementation: a real monthly stream confirms continuity across midnight, but
does not confirm monthly/daily overlap, cross-file reordering, or old-tail after
the grace period.

`EventLineCoordinator` has one reader/worker and a monotonic sequence per
`PlayerProfileId`; different profiles can be processed independently. Display
names are not runtime identity keys. A consumer error does not stop other
profiles: after `Process` fails, the coordinator calls `Invalidate(profile)`,
and faults the consumer/profile only if invalidation also fails.

## Nearby Scan

`NearbyScanProcessor` is an independent ordered Prospecting consumer. It parses
timestamp-normalized `There is ... nearby.` lines into a raw description and an
ordered, duplicate-preserving list of material entries with optional quality.
`There is only rock nearby.` is represented explicitly. Per profile, only the
latest valid snapshot is retained and shown in the `Nearby Scan` section after
Vein Context; a profile with this snapshot alone remains visible. Reset Session,
Reset Profile/Vein Context, reconnect, `Logging started`, and
`StreamContinuityLost` clear it. A local Nearby Scan/Only rock line also clears
the same profile's ActiveVeinContext, Vein MiningAttempt, and partial
Prospecting; it never decrements Remaining or changes Mining materials, Total,
or Moment of Inspiration. `You start to mine.` (Surface) and `You start to
tunnel.` use the same cave-state invalidation and do not create a Vein attempt.
`You start to mine the ceiling.` instead clears only the matching stale Nearby
snapshot, preserves the existing wall Vein Context, and creates its existing
unbound Ceiling attempt. A synchronized direct Prospecting observation likewise
clears only the matching stale Nearby snapshot after it establishes or updates
the Vein Context. Prospecting start, an assembler waiting for its first Material,
and a complete MaterialOnly Vein observation do not clear Nearby. This per-profile callback occurs while processing the same ordered
coordinator item; it creates neither another ingress nor another Nearby state.
The clear itself does not mutate Mining counters, TotalCount, Moment of
Inspiration, or Vein Context.
Queue tunnelling announcements and third-person tunnelling do not invalidate
state. Analyse traces are a separate feature and are not routed to Nearby
Scan.

Nearby snapshots may receive contiguous exact `You will find salt here!`
and `You will find flint here!` replacements, including OnlyRock snapshots. The
owner is per profile and distinct from the long-lived presentation snapshot: the
first non-augment line closes eligibility, while the snapshot remains visible
under its existing lifecycle. Augments preserve first-seen order, duplicates do
not publish a replacement, and no timeout or timestamp association exists.

## Prospecting augment ownership

Augments use the physical ordered Event stream only. Known-Quality PlainRock
and Nearby snapshots accept contiguous Salt/Flint; a finalized normal,
non-RockSalt Direct Vein with ExactMax and known Remaining accepts contiguous
Salt. Direct Flint and RockSalt Flint are not implemented. Each owner is per
profile and is closed by the first unsupported/non-augment line, all hard
boundaries, continuity loss, reset, reconnect, `Logging started`, or consumer
invalidation. Replacement is immutable, does not create a Vein for PlainRock,
and does not alter Mining counters, Remaining, or MiningAttempt. Existing UI
receives the snapshot changes but does not yet display augments.

## Live Skill Statistics — v1.8.0

`LiveSkillMonitor` is the sole application-lifetime owner of production `_Skills` reads. Discovery catalogs candidate `_Skills` files, but activation requires the profile's console log to declare the active writer with a valid `Writing to <path>` record. For each profile it retains source registrations, the active writer, continuity state, and one `SkillStateTracker`. A rebind after controlled retirement requires a fresh declaration; retained Statistics do not imply active monitoring.

At profile initialization, existing canonical `_Skills.YYYY-MM.txt` and `_Skills.YYYY-MM-DD.txt` files receive startup boundaries. Historical content may be used only to bootstrap Current Mining. Bootstrap never creates Last Gain, Session Gain, or `PositiveUpdateCount`. A source that existed at startup but whose boundary could not be captured reliably is not later reinterpreted as a new post-start source.

Live numeric updates have the same semantics for all skills:
- Current = ostatnie `to X`;
- Last Gain = the most recent positive `increased by X` value;
- Session Gain = suma dodatnich live increases;
- PositiveUpdateCount = liczba dodatnich live updates;
- `decreased` updates Current but does not increase Last Gain or Session Gain.

`ObservationStartedAtUtc` is set once when a profile is discovered. `MiningStatisticsSessionBaselineStore` retains an application-lifetime, in-memory per-profile boundary for raw Mining ticks, raw Mining gain, and the current Mining Session start time. Without an explicit reset, the time boundary is `ObservationStartedAtUtc`; Reset Session records a new baseline. Raw `PositiveUpdateCount` and raw `SessionGain` are not reset.

`SkillStatisticsViewModel` combines the immutable snapshot, this baseline, and the application-lifetime `MiningSessionCounter`. For Mining it shows gain/hour, the strict next milestone (0.50 below 99.00 and 0.01 from 99.00 to 100.00), ETA, and session-relative Successful actions, Mining ticks, Tick rate, Actions / tick, Avg gain / tick, Average ticks / hour, and Avg ticks / N actions normalization. The Statistics window's one-second timer refreshes time-based metrics only; it does not read files or poll sources.

`MainViewModel` and `SkillStatisticsViewModel` subscribe to `ProfileChanged`, marshal changes to the UI dispatcher, and fetch the current snapshot again so stale queued events are not applied. Detach uses version guards, so a callback queued before shutdown cannot mutate the UI after detachment.

The monitor polls serially every 250 ms. Console tailing supports append, partial UTF-8, truncation/replacement, and bounded oversized records. Shutdown closes admission for new notifications, cancels the worker, and safely handles `Dispose` called both externally and from its own callback/worker.

The old manual Mining Statistics runtime, Mining Action Statistics, and separate `LiveMiningMonitor` were removed in v1.7.0. No production Start/Stop/Reset/New Session/source-selection path or second `_Skills` monitor exists.

## Mining and session

The parser recognizes exactly: `StoneShards`, `Ore`, `Shards`, `RockSalt`, `Salt`, `Flint`, `SourceCrystal`, `Sapphire`, `Diamond`, `Emerald`, `Ruby`, and `Opal`. Both confirmed literals, `You mine some pink crystals.` and `You get a source crystal.`, increment the same canonical `SourceCrystal` counter.

Each recognized event increments its own material counter by `+1`. Only primary materials (`StoneShards`, `Ore`, `Shards`, `RockSalt`) increment `TotalCount`; byproducts (`Salt`, `Flint`, `SourceCrystal`, `Sapphire`, `Diamond`, `Emerald`, `Ruby`, `Opal`) add zero to `TotalCount` and do not change Remaining. `Ore` and `Shards` intentionally remain generic. Matching is case-sensitive; a valid `[HH:mm:ss]` timestamp is optionally removed. Other-player, incomplete, and unknown lines are ignored. `SourceCrystal` is a byproduct; its actual action source is undetermined.

`MiningInspirationTracker` correlates only this per-profile sequence: `Mining/Tunnelling start → You have a moment of inspiration... → qualifying local primary yield`. MOI alone, a byproduct/gem, or third-person Mining does not create a result. A confirmed sequence normally increments the primary counter, may perform one existing eligible Remaining decrement, and separately adds `Moment of Inspiration +1` to the internal `MiningInspirationCounter`. The real `The wall will break soon.` message is an informational warning for the same action and preserves pending MOI until the primary yield; `The wall breaks!` was not added to the correlation. Real-log replay after this change counted ProfileA 5, ProfileB 2, and ProfileC 0. Rarity tiers Rare/Supreme/Fantastic remain **DATA-NEEDED**.

Session state lasts for the WurmMiningCounter process lifetime, including gaps caused by pausing or restarting the Wurm client. No input alone resets state or releases a profile. **Reset Session** enqueues per-profile reset controls in the Mining and Context FIFOs; each lane preserves its local before/reset/after ordering. For an Event-backed Mining profile, the order is: previously admitted outputs, capture one immutable skill snapshot and one timestamp for tick/gain/time baselines, the existing Mining reset / `TotalCount` clear, then later outputs. A skill-only profile receives the same complete boundary without creating artificial Event state. Reset Session is distinct from natural source disappearance and controlled retirement: it resets session-derived state but does not release the source or remove its active Event checkpoint. Natural/controlled source release removes the active Event state/checkpoint and follows its owner-release lifecycle. Neither operation replays history.

Settings and `WindowPlacement` persist, but Mining session state is not currently persisted between runs. On application startup, `Total` and material counters begin a new session; `ActiveVeinContext`, `MiningAttempt`, pending Prospecting, depletion tracking, and transient UI state are not restored either. The Wurm world may change while the application is inactive, so automatically restoring an old context could present stale data. Explicit Save/Resume Session remains a **FUTURE FEATURE** requiring a separate design and stale-state validation.

`MainViewModel.ResetMaterials(profile, materials)` delegates to
`MiningSessionCounter.ResetMaterials(profile, materials)`. This shared selective
reset operation is used by both ResetWindow and Quick Reset from the context
menu. Under the lock, it removes only the selected `MaterialCounts` entries (it
does not store zero values), preserving `TotalCount` and all other materials.
An empty selection, missing profile, or missing material is a no-op;
`CountsChanged(profile)` fires exactly once and only after an actual change.
Selective reset does not change `Mining Inspirations`, pending MOI,
`ActiveVeinContext`, Remaining, Nearby Scan, `MiningAttempt`, or Prospecting
state.

There are no two parallel mechanisms for removing material counters:
`ResetProfileMaterialCounters(profile)` uses the same lower-level primitive for
all materials in a profile. Full material reset preserves Total, but the
existing `MainViewModel` layer still clears pending MOI. `ResetProfileAsync(profile)`
inserts an ordered `ResetProfileBarrier` only for that profile and clears Vein
Context/tracking while preserving counters and Total. The application-level
session reset orchestrates the local FIFO reset controls without a global
cross-worker rendezvous.

## Prospecting Results presentation

The `Prospecting Results` and `Nearby Scan` headings each expose the same
per-profile `Reset Prospecting Results` action. It awaits the existing ordered
Context reset barrier, which clears that profile's Vein, PlainRock, and Nearby
state. Exact normalized `You enter the cave.` and `You leave the cave.` lines
clear the same transient state synchronously in the ordered Context consumer.
Both boundaries preserve Mining counters, Moment of Inspiration, and other
profiles. Reset menu identity is captured as `PlayerProfileId` when the menu
opens, so a profile-panel rebuild or duplicate display name cannot redirect
the action.

`VeinContextProcessor` remains the owner of genuine `ActiveVeinContext` and
also retains the last PlainRock result, including MaterialOnly, per profile solely for
presentation. `MainViewModel` propagates both kinds of information to the
matching profile panel. `Prospecting Results` is therefore a presentation layer
broader than `ActiveVeinContext`: it can display either the real Vein Context or
PlainRock. MaterialOnly PlainRock displays `Plain Rock` without Quality; a later
Grade or ExactMax replaces it with the more precise presentation. PlainRock
presentation state is not a
Vein Context. It has no Remaining, confidence, `VeinMaterial`, MiningAttempt
binding, yield matching, or Exact Remaining decrement.

The transient PlainRock result is replaced by newer MaterialOnly, Grade, or
ExactMax PlainRock, cleared when a Vein synchronizes, and cleared with the
existing hard `ClearState` boundaries. A new Prospecting start alone does not
clear it. It is per profile and is not persisted/restored at application startup.
After a PlainRock result, TypeOnly/Partial Vein Context has presentation
precedence while PlainRock remains internal fallback until synchronized Vein or
clear; no stale/resurfacing PlainRock behavior is confirmed.

Prospecting result snapshots may carry immutable Salt/Flint Augments, but the
current UI deliberately does not render them. The replacement events use the
existing view-model refresh path and do not alter Mining counters.

## UI

After an actual `MiningSessionCounter` change, the UI refreshes the matching profile through the Dispatcher; an unchanged snapshot does not replace the read model or panel. The `MainWindow` one-second timer remains only a fallback/reconciliation mechanism. `MainWindow` uses lightweight profile `GroupBox` controls, with material names on the left and counts on the right. A profile is visible when it has a cumulative Total, a positive material count, a positive Moment of Inspiration count, or a `Prospecting Results` value; materials with `Count <= 0` are hidden. `Moment of Inspiration` is a separate per-profile row, shown only for positive values after Materials and before `Prospecting Results`. `Prospecting Results` appears inside the matching profile panel, not in a separate list. Its real-vein branch presents Vein Context; its PlainRock branch does not create or present Vein Context.

The currently available selective material reset is `Quick Reset` from the material-name context menu. The older `ResetWindow` and its tray entry are not currently available from the production menu. `Reset Session` remains available under Settings > General and requires confirmation.

The material name in `MainWindow` has a compact `Reset <Material> counter` context menu. It is attached only to the name area, not the count or full row; only `Ore` has a local `MinWidth=48` hit area. The menu has one action, local `ContextMenu`/`MenuItem` templates, a uniform background, and `Placement="MousePoint"`, without affecting other application menus. Quick Reset asks for confirmation, showing the material name and current value and explaining that Total and other counters are preserved; cancel/close is a no-op. On confirmation it calls exactly `MainViewModel.ResetMaterials(profile, [material])`; the UI contains no counter-mutation logic of its own.

The window uses `SizeToContent="Height"`: with no profiles it collapses to the header, grows with profile sections up to `MaxHeight=620`, and then uses vertical scrolling only. `StatusText` is not displayed directly; the header contains a colored dot: green for `Monitoring`, gray for `Monitoring unavailable`, and red for `Monitoring error`. The dot's tooltip gives the full status description; unknown status remains gray.

Vein Context remains a read-only sidecar presentation. A recognized Prospecting
Material can itself be a complete lower-precision MaterialOnly Vein observation:
audit 73A confirmed 13 real sequences ending at an action boundary without
Quality or Remaining. It produces `TypeOnly` with Unknown Quality/Remaining;
UI shows Material and confidence while hiding those Unknown values. TypeOnly is
unbound and never participates in local Exact Remaining decrement. Grade and
EstimateBand contexts, and exact contexts, are optional precision upgrades.
Estimated Remaining is displayed as its approximate range and is never locally decremented. For an explicitly
signalled local Exact decrement, `RemainingText` transiently shows `↓N` before
the current value; the series resets after approximately one second of no new
local decrement. Re-prospecting, resynchronization, replacement, clear,
EstimateBand, and Unknown do not show that indicator.

`VeinContextProcessor.ApplyMaterialObservation` is the single material
lifecycle policy for direct Prospecting updates, `VeinEmergenceParser`, and
`VeinExamineParser`. A different material replaces the context as TypeOnly and
cancels a differently bound MiningAttempt before the replacement; the old
attempt cannot later mutate the new context. A same-material observation is a
benign confirmation and preserves Exact/Synchronized/Tracking data.

## Tray and Settings

The tray menu contains **Always On Top**, **Alerts**, **Statistics**, **Settings**, and **Exit**. A single left-click on the tray icon toggles `MainWindow` visibility.

- When the tray is available, closing `MainWindow` hides the window and monitoring continues.
- When the tray is unavailable, closing `MainWindow` starts application shutdown.
- Hiding the window does not stop monitoring.

`Resources/WurmMiningCounter.ico` is the shared final icon for the EXE, `MainWindow`, and `NotifyIcon`; the tray does not use `SystemIcons.Application`.

Settings has a General tab with `AlwaysOnTop` and `StartMinimizedToTray`, and a
Diagnostics tab with `DiagnosticsEnabled`, Enabled/Disabled status,
`Entries: N / 1000`, `Storage: Memory only`, Open Diagnostics Window, and
confirmed Clear Diagnostic Log. `SettingsWindow` and the non-modal
`DiagnosticsWindow` are independent normal windows (`Topmost=false`, with no
`Owner=MainWindow`); Always On Top applies only to `MainWindow`.

Settings changes are saved automatically to
`%AppData%\WurmMiningCounter\settings.json`; `AlwaysOnTop` takes effect immediately, while
Start Minimized takes effect on the next launch. `SettingsService.Load()` keeps
a safe fallback, and Load/Save report JSON, I/O, and permission failures to
`RuntimeDiagnosticService`; `Save()` returns `bool`. Runtime commits changed
settings only after a successful write. On failure, the UI uses the existing
`Settings save failed` status, and Always On Top returns to its previous value.
`WindowPlacement` is part of the same model but is not exposed in Settings UI.
`Reset Session` is available under Settings > General and requires confirmation.
`Statistics` opens or activates the single automatic `SkillStatisticsWindow`,
and `Alerts` opens or activates `AlertsWindow`.

## Window placement

Future Settings UI rule: when tab content no longer fits vertically, the tab
content must use `VerticalScrollBarVisibility="Auto"`; Settings horizontal
scroll remains disabled, and the window should not be enlarged automatically
without a concrete layout need.

Only `Left`, `Top`, and `Width` are saved. Height always follows `SizeToContent="Height"`, so it is neither saved nor restored. Older JSON containing `WindowPlacement.Height` is read successfully, but the field is ignored and removed on the next save. Position and width writes are debounced, do not occur on `Hide()`, and restoration happens before the first `Show()`. Placement must have finite values, a minimum width, and a visible area of at least 64x32 DIP, calculated from the window's minimum height. `WindowState` is not persisted.

## Single instance

By default, `SingleInstanceService` uses `Local\WurmMiningCounter` and the `WurmMiningCounter.Activate` pipe. A secondary instance sends `ACTIVATE` and exits before creating Settings, `MainWindow`, the monitor, or tray. The primary is shown and activated through the Dispatcher. There is no legacy, takeover, or version protocol.

Optional environment variables for the mutex, pipe, and settings directory support isolated smoke tests; the defaults are used when they are unset.

## Startup and shutdown

The composition root creates the domain consumers, alert delivery, popup host,
and module lanes before starting Event Pipeline. An explicit exit begins
idempotent teardown before calling WPF `Application.Shutdown()`:

```text
RequestShutdown / PrepareForShutdown
-> MainViewModel detaches LiveSkillMonitor notifications
-> LiveSkillMonitor stops polling and drains active notification dispatch
-> EventPipeline stops source polling and completes FastEventDispatcher
-> MiningEventLane and EventLineCoordinator drain
-> EventAlertDelivery closes admission and drains accepted alerts
-> EventAlertPopupHost clears pending UI work and closes its active popup
-> TrayIconService -> SingleInstanceService
-> WPF Application.Shutdown()
```

The delivery worker only admits popup notifications; shutdown does not wait
for the visible popup lifetime. Popup cleanup runs on its owning UI dispatcher
before WPF begins closing windows. Late popup and timer callbacks are ignored.

### Startup primary

1. `SingleInstanceService` is created first.
2. A secondary instance sends `ACTIVATE`, calls `Shutdown()`, and creates no other services.
3. The primary loads Settings, creates `PlayerProfileIdentityResolver` and `LogDiscoverySnapshotService`, then discovers a versioned startup snapshot from the configured locations.
4. It creates the Mining/Context domain consumers and Event modules, then constructs `LiveSkillMonitor` with the snapshot candidates.
5. It constructs `MiningEventLane`, `FastEventDispatcher`, and `EventPipeline`, passing the same snapshot candidates to Event source registration.
6. It creates `MainWindow` and Monitoring Health wiring, initializes the tray and window placement, then applies the configured initial window visibility.
7. After UI and services are composed, it starts `LiveSkillMonitor`, starts `EventPipeline`, and begins serialized snapshot reconciliation.

`ShutdownMode.OnExplicitShutdown` supports tray mode. `OnExit` invokes the same
idempotent teardown again as a final safeguard; closing windows does not start
normal shutdown.

Important `IDisposable` services have idempotent lifecycles. Callbacks do not
start new work after shutdown begins. Primary startup reports an unhandled
pipeline-initialization failure to `RuntimeDiagnosticService`; if `MainWindow`
exists, it shows the existing `Startup error` status, otherwise startup exits
cleanly after recording diagnostics. Normal secondary-instance exit is
unchanged. Each shutdown step is isolated: a `Dispose`/`CompleteAsync` failure
is reported to `RuntimeDiagnosticService` and does not block the next cleanup
step.

## Out of scope

Steam installation/library discovery is implemented for configured Steam log-location rules; the application does not detect whether a Wurm client process is running. There is no `PlayerScanner` implementation.

## Release packaging

Release artifacts are kept outside Git in the version directory specified by
[RELEASE_PROCESS.md](RELEASE_PROCESS.md). Each Windows x64 release has three
variants, all published as single files.

| Variant | Publish profile | Properties | Required runtime |
| --- | --- | --- | --- |
| Full | `win-x64` | self-contained, single-file | No separate .NET Runtime installation required |
| Compressed | `win-x64-compressed` | self-contained, compressed single-file | No separate .NET Runtime installation required |
| Lite | `win-x64-lite` | framework-dependent, single-file | Microsoft .NET 8 Desktop Runtime x64 |

Each variant's runtime package contains exactly `WurmMiningCounter.exe`, the
unchanged root `LICENSE` (MIT), the current `README.md`, and an empty `Sounds/`
directory; users may add their own WAV files directly to it. The README links
to documentation hosted in the project repository. The ZIP contains an
explicit empty `Sounds/` directory entry. Release notes and other documentation
are not part of runtime packages.

The local artifact layout for a `<version>` is fixed:

```text
C:\Programowanie\RELEASES\WurmMiningCounter\<version>\
  Full/
    WurmMiningCounter.exe
    LICENSE
    README.md
    Sounds/
  Compressed/
    WurmMiningCounter.exe
    LICENSE
    README.md
    Sounds/
  Lite/
    WurmMiningCounter.exe
    LICENSE
    README.md
    Sounds/
  WurmMiningCounter-v<version>-win-x64.zip
  WurmMiningCounter-v<version>-win-x64-compressed.zip
  WurmMiningCounter-v<version>-win-x64-lite.zip
  SHA256SUMS.txt
  ReleaseNotes.md
```

### Release procedure

1. Set the version in project metadata and in release documents that refer to the current version.
2. Run a Release build, then the full test suite.
3. Check the change scope, commit release preparation, and build all three
   variants from that release commit using their corresponding publish profiles.
4. Copy the root `LICENSE` and current `README.md` into each published
   directory and ensure `Sounds/` exists and is empty. Create each ZIP with
   exactly the EXE, `LICENSE`, `README.md`, and an explicit empty `Sounds/`
   directory entry. Use the filenames shown above;
   write package hashes to `SHA256SUMS.txt` and keep `ReleaseNotes.md` outside
   the variant directories and ZIPs. Verify the EXE version, LICENSE SHA256,
   exact directory/archive contents, and sizes.
5. Create and push an annotated tag for the release commit, then publish the
   three ZIPs and checksum file from the version directory.
