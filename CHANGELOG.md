# Changelog

## [Unreleased]

### Privacy

- Anonymize real profile names in documentation and tests, and ignore dated Wurm channel logs and common local secret files.

## [1.9.0] - 2026-11-21

### Fixed

- Isolate WPF window tests from concurrent XAML loading in the test suite.
- Use short separators in profile selection, location diagnostics and Analyse map tooltips.
- Use a short ASCII hyphen between Analyse trace strength and material.
- Improve Analyse observation formatting and simplify detected vein details.
- Set the Analyse window minimum height to 604 DIP.
- Preserve confirmed Analyse traces without a compass direction and indicate their unknown map positions.
- Recognize the confirmed vague Analyse trace of unidentified material.
- Recognize the confirmed faint Analyse no-trace message.

### Added

- Add an always available Analyse shortcut to the tray menu.
- Show the completed Analyse finish time when its log line has a valid timestamp.
- Parse confirmed Analyse shard and ore boundaries, stage markers, traces, and no-trace messages as observational data.
- Assemble and publish per-profile completed Analyse observations through the existing ordered Context lane.
- Add a standalone Analyse results window and a compact per-profile MainWindow button for completed observations.

### Documentation

- Clarify Analyse directionless observations and the UI's approximate distance rings.
- Clarify that Analyse displays recognized log information and may omit unsupported messages.
- Record Wurmpedia Analyse findings, evidence limits, and observational sequence decisions.
- Record the per-profile Analyse result window and button behavior.
- Update Analyse implementation status in the README, architecture, roadmap, and specification.

### Validation

- Cover synthetic interleaving of Analyse with Mining on an active vein.
- Cover Analyse through Event file ingestion and Context FIFO to the results window, including Mining and reset isolation.

## [1.8.4] - 2026-11-08

### Added

- Recognize observed Adamantine and Glimmersteel emergence and Prospecting messages through the existing Ore rules. Pickaxe sweet spot is unavailable until their difficulties are confirmed.

### Fixed

- Show when an Event ownership conflict pauses monitoring or a confirmed owner change requires a restart.
- Discard redundant Event file conflict entries and resolved segment entries after those segments are released.
- Report lost Event logs bindings as suspended and retry verification on the existing schedule or new confirmed evidence.
- Verify Event ownership and linked logs before accepting or retrying file disappearance.
- Prevent ambiguous ownership or unreliable physical identities from authorizing discovery retirement intents.
- Allow controlled Event retirement after an ownership conflict has been authoritatively resolved.
- Suspend an Event directory when discovery confirms a different owner; a fresh startup establishes the new owner's EOF baseline.
- Verify discovered Event log-directory bindings before baseline and subsequent reads.
- Resume a suspended Event source from its existing checkpoint when discovery confirms its original owner.
- Suspend Event reads when discovery reports conflicting source ownership.
- Prevent uncertain Steam metadata or unavailable libraries from retiring discovered sources.
- Retain discovery sources when a previously available configured location is temporarily missing.
- Preserve discovery reconciliation state when physical profile identity is missing or conflicting.
- Preserve the owning profile identity and console path when its logs directory resolves to a linked location.
- Complete unbound Wall mining attempts on primary results without attributing a vein or decrementing Remaining.
- Clear stale Prospecting results and vein context on `You see dark dungeons.` while preserving in-flight Prospecting, unless a Mining attempt is in progress or awaiting its final primary.
- Make Event retirement tests independent of the calendar date by matching test clocks or file names to the active segment.

### Validation

- LIVE validation was intentionally omitted for this release.

## [1.8.3] - 2026-10-17

### Added

- Added serialized App execution of complete, versioned Discovery retirement
  intents, including Event FIFO-drain-gated reappearance.
- Added test-only controlled Event retirement with asynchronous FIFO-drain gating.
- Added a test-only, generation-checked Skills source retirement and reappearance contract.
- Added versioned, fail-closed Discovery reconciliation evidence using opaque
  physical profile-directory keys and non-destructive suppression intents.
- Added a passive shared log-discovery snapshot for manual location discovery.
- Added a fail-closed, local registry for stable discovered player-profile identities.
- Added validation and conflict reporting for explicitly configured Wurm data
  locations, including Wurm, players, and profile logs paths.
- Added persisted, declarative Wurm Online and Wurm Unlimited log-location
  rules, with first-run defaults and Steam-library manifest discovery.

### Changed

- Manual Find locations now additively reconciles newly discovered Event and
  Skills candidates into running monitors without restarting existing sources.
- Event transport now carries `PlayerProfileId` through EventPipelineV2,
  dispatcher, Mining FIFO, and ordered Context lanes; legacy name-based control
  calls are retained only for an unambiguous single profile.
- Skill Statistics retains the selected `PlayerProfileId` while live Skills
  snapshots refresh its profile list.
- Additional Wurm data locations now persist in application settings and are
  retained when other settings are saved.
- Settings writes now retain configured log-location rules.
- Log location management now uses a shared, resizable manager window with
  aligned discovery summaries and compact source and profile results.

### Hardened

- Strengthened LD regression coverage for exact Event release identity, WPF
  same-name profile selection, and real directory-link discovery fixtures.
- Log-location discovery now marks rejected access/I/O roots as incomplete and
  labels partial results accordingly; incomplete scans retain prior rule
  detections instead of confirming their absence.
- Monitoring health now follows the current Event and Skills source lifecycle;
  terminal faults retain error priority over later Identity recovery, and stale
  UI callbacks cannot restore Monitoring after shutdown or a newer fault.
- Confirmed Skills replacements whose startup capture temporarily fails now retry
  through the existing pending registration path; repeated pending failures are
  deduplicated by profile and exception kind.
- Identity-blocked Event or Skills sources now surface a bounded diagnostic and
  monitoring health state, while unaffected profile sources continue running.
- Failed Skills profile initialization no longer leaves a directory registered
  as active; confirmed registrations can retry without weakening fallback lease rules.
- Log discovery retains profiles found before an enumeration failure, reports
  access/I/O failures distinctly, and marks affected results incomplete.
- Session reset now clears retained Mining, Inspiration, Prospecting, and Nearby
  state after an Event source release without recreating an unnamed profile lane.
- Event and Skills now share guarded profile ownership registrations: temporary
  File ID loss suspends confirmed sources instead of creating another identity,
  and physical owner replacement re-registers at EOF without replaying history.
- Controlled Event release failures now reach Monitoring Health exactly once;
  Skills retirement and same-ID rebind publish current source state while
  retaining historical Statistics. Oversized unterminated Skills records and
  completed terminal-fault history are bounded without changing active fault
  transfer or FIFO release behavior.
- Path-scoped fallback sources retain a shared directory namespace guard; persisted
  unconfirmed identities cannot activate after that continuity lease ends without
  explicit identity reconciliation. Read guards also protect junction aliases.
- Skills owner replacement retires the old source, requires a fresh writer declaration,
  and preserves session aggregates when a previously observed physical owner returns.
- Player identity registry validation now rejects duplicate profile IDs across
  entries without modifying the invalid file; confirmed directory aliases retain
  their shared identity through the existing single-entry resolution.
- Settings saves now fail closed after an invalid or unreadable existing settings file,
  and Steam location paths must remain inside their declared installation.
- Steam discovery now validates complete Valve KeyValues metadata, registry and
  fallback Steam roots, library manifests, and bounded per-pass metadata reads.
- Log discovery now normalizes Windows paths and deduplicates physically identical
  directory aliases by File ID without merging paths whose physical identity is unknown.
- Windows-specific directory identity probing is isolated behind a local adapter;
  discovery keeps platform-neutral opaque identity values.
- Settings with a null or non-object JSON root block subsequent automatic saves.
- Log discovery deduplicates logs across all players roots, reports unknown logs
  identity and ambiguous profile ownership, and retains logical input paths.
- Steam discovery bounds VDF nesting, retains registry and I/O diagnostics, reads
  both known library-list locations as independent metadata sources, and deduplicates
  physical library aliases before reading manifests.
- A failed profile directory no longer discards valid profiles under the same players root.
- Discovery now catalogs recognized top-level Event and Skills log files for
  unambiguous player sources without selecting an active writer.

## [1.8.2] - 2026-10-10

### Added

- Added an independent per-profile Moment of Inspiration reset ordered through
  the profile's Mining FIFO.
- Added unified per-profile Reset Prospecting Results menus for Prospecting
  Results and Nearby Scan, backed by the existing ordered Context reset barrier.
- Entering or leaving a cave now clears the affected profile's Prospecting and
  Nearby Scan state.

### Changed

- Canonicalized both confirmed Source Crystal log aliases to one `SourceCrystal`
  byproduct counter, displayed as `Source Crystals`.
- Reset menus now retain the selected profile across profile-panel rebuilds.

## [1.8.1] - 2026-10-09

### Changed

- Custom Event Alerts now match case-insensitive whole words or contiguous
  phrases after Event-line normalization. New custom triggers require one word
  of at least three characters, or at least two words; legacy short patterns
  remain loadable but do not match.
- New custom Event Alerts default popup delivery to disabled. Popup bursts retain
  one active and at most two pending notifications, and popup windows do not
  activate themselves.

## [1.8.0] - 2026-10-08

### Added

- Added live Pickaxe sweet spot guidance in MainWindow for recognized veins,
  calculated from the current Mining skill and vein difficulty.
- Added live Mining tick count in Skill Statistics, counting positive live Mining
  updates in the current application session.
- Added Mining Session Statistics: Successful actions, Mining ticks, Tick rate,
  Actions / tick, Avg gain / tick, Average ticks / hour, and configurable
  Avg ticks / N actions normalization.

### Changed

- Reset Session now establishes a fresh session-relative baseline for Mining
  ticks, Mining gain used by derived statistics, and Mining session start time.
- Raw `PositiveUpdateCount` and raw `SkillStateTracker.SessionGain` remain
  application-lifetime aggregates; existing `TotalCount` reset semantics are
  unchanged.
- Skill Statistics remembers the last selected profile while WMC remains running
  and restores it when the window is reopened, falling back safely if the
  profile no longer exists.
- Redesigned Skill Statistics into a compact vertical layout with an improved
  profile selector, semantic gain colors, and a refined Other Skills table.

### Hardened

- Reset boundaries capture Mining tick, gain, and session-start baselines together.
- Isolated baseline-notification subscriber failures for Event-owned and
  skill-only profile resets.
- Added deterministic coverage for custom N parsing and baseline updates.

## [1.7.0] - 2026-10-07

### Added

- Added automatic live Skill Statistics backed by one application-lifetime
  `LiveSkillMonitor`. Statistics now show Current, Last Gain, and Session Gain
  for every observed skill, with deterministic multi-profile selection.
- Added Mining gain per hour, the next automatic milestone, and milestone ETA.
  Milestones advance by 0.50 below 99.00 and by 0.01 from 99.00 to 100.00.

### Changed

- MainWindow Mining Current and Session Gain now update from live skill events
  at the monitor's 250 ms cadence instead of depending on its one-second UI
  refresh timer.
- Existing `_Skills` history now supplies only bootstrap Mining Current at
  observation start. Historical updates never create Last Gain or Session Gain;
  subsequent live updates track all numeric skills.
- Tray Statistics now opens the automatic Skill Statistics window. Manual
  Start, Stop, Reset, New Session, source selection, and dump workflows were
  removed.

### Fixed / Hardened

- Hardened `_Skills` startup boundaries, source continuity, partial records,
  UTF-8 BOM handling, console replacement detection, bounded oversized console
  records, and monitor shutdown/event lifecycle handling.
- Removed the retired manual Statistics runtime and duplicate Live Mining
  monitor path.

## [1.6.0] - 2026-10-06

### Added

- Configurable Event Alerts with protected built-in Wall Break triggers,
  custom triggers, per-trigger delivery controls, and an in-app editor.
- Popup and optional sound delivery, including dynamically discovered
  user-provided WAV files from the application Sounds folder.

### Changed

- Replaced the legacy EventLogMonitor production path with EventPipeline and
  FastEventDispatcher. New Event log entries use bounded, ordered per-profile
  processing with source continuity checks and approximately 250 ms polling.
- The tray now provides Alerts and Statistics alongside Always On Top,
  Settings, and Exit. MainWindow close hides to tray when the tray is available.
- Runtime packages now include an empty Sounds folder; no default WAV files
  are bundled. The compressed win-x64 self-contained single-file profile is
  available for release.

### Fixed / Hardened

- Event Alerts use exact matching after shared normalization of the standard
  Event-log timestamp prefix. Settings preserve custom triggers and built-in
  delivery choices, and malformed alert entries do not prevent startup.
- Alert queues, popup lifecycle, overload handling, and pre-WPF shutdown
  teardown are bounded and fault-isolated.
- Wall Break handling now preserves active-context ownership and avoids stale
  context updates after a later compatible yield.

### Verified

- Full Release suite: **1430/1430 PASS**.
- Release build: **0 warnings, 0 errors**.
- Compressed self-contained release candidate smoke: **PASS**.
- Manual release smoke: **PASS**.

## [1.5.0] - 2026-09-14

### Added

- Added contextual Prospecting augment upgrades on the ordered Event stream.
  Known-Quality PlainRock and Nearby snapshots accept contiguous exact Salt/Flint;
  finalized normal Direct Veins with ExactMax and known Remaining accept contiguous
  Salt. Replacements are immutable and per profile, duplicates are no-ops, and the
  first unsupported/non-augment line closes ownership. No timeout, timestamp
  heuristic, Mining counter, Remaining, or MiningAttempt behavior was added.
  Direct Flint and RockSalt Flint remain data-needed; existing UI does not yet
  display augments.

- Added app-lifetime Live Mining in MainWindow: per-profile Current Mining and
  positive Mining Gain from startup, sourced from the exact valid monthly/daily
  `_Skills` writer declared by `console.<profile>.log`. The monitor baselines
  existing sources at EOF, handles declared source switches, isolates profiles,
  marks continuity loss Incomplete, and remains independent of manual Mining
  Statistics. ProfilePanel shows one compact Live Mining row above materials.

- Added regression coverage for eight production-path Stone Shards successes:
  material count and Total remain equal at every counter publication and
  ProfilePanel refresh.

- Added the App-owned manual Mining Statistics session UI: explicit profile and
  exact canonical `_Skills` source selection, one reusable tray window, Start /
  Reset / Stop / New Session controls, and shutdown cleanup. Closing the window
  does not stop an active session.

- Added monotonic Session Duration, Mining gain (`PositiveGain`), Net Mining
  change (`NetGain`), and raw Observed Mining gain / hour. Decreased Mining
  ticks remain in Net Mining change without lowering Mining gain or its rate.

- Added next-whole Mining targets plus session-local decimal user targets and
  ETA at observed Mining gain rate. Reset retains the user target; New Session
  clears it. `Reached`, unavailable, and out-of-range results are explicit;
  `Incomplete` suppresses ETA and terminal/fault snapshots remain frozen.

- Added atomic runtime snapshot publication, safe Dispose publication, and
  `TimeSpan.MaxValue` saturation for elapsed duration outside its representable
  range. `_Skills` discovery is newest-first with deterministic filename
  tie-breaking.

- Added the final 450 px Mining Statistics presentation: header duration,
  three KPI cards, inline Runtime/End status, PNG section icons, and text-only
  action buttons.

- Added session-local Total Mining Actions to Mining Statistics Tick Statistics.
  The App-owned session reuses the existing Event pipeline,
  `MiningStatisticalActionParser`, and action counter for the selected profile.
  Neutral per-profile FIFO markers define Start, Reset, and Stop boundaries;
  terminal, fault, cap, incomplete, and Dispose states freeze the sample, while
  New Session creates a fresh sample.

- Polished Mining Statistics presentation: centered blue Current skill with
  green `+Mining gain`, centered Time to target and Observed gain / hour,
  readable ETA, decimal display capped at six fractional digits without trailing
  zeros, a 62 px target input, and local ETA-row spacing.

- Added the local MS-3D1 `MiningSkillsSessionRuntime`: one explicitly selected
  profile and canonical `_Skills` source, exact profile/logs validation, one
  `PeriodicTimer`, one background worker, serialized coordinator access, and
  EOF-baseline-before-polling without history replay. Failed baseline can retry
  `Start`; a successfully started instance is non-restartable; `Reset` clears
  aggregation without rewinding the reader or rebaselining.

- Added the isolated `WurmMiningCounter.MiningStatistics` foundation: its own
  statistical action model and ten-literal start parser, per-profile in-memory
  action counter, snapshots/resets, and a passive ordered-stream observer.
  `Meteorite` remains statistics-only and existing Mining/Prospecting behavior
  is unchanged.
- Added the local MiningStatistics Skills Session Core: strict numeric `_Skills`
  update parsing, in-memory Mining/Other Gains aggregation, immutable snapshots,
  and reset. It has no production tailer, watcher, UI, persistence,
  action-count integration, or runtime registration.
- Added the local MiningStatistics Skills Session Lifecycle: immutable
  profile/source identity, `Idle → Observing → Stopped` and `Observing →
  Incomplete` transitions, frozen terminal snapshots, explicit stopped reasons,
  and a 4096-positive-Mining-tick exact-median cap. It remains independent of
  `_Skills` I/O and production registration; death-event correlation is not
  implemented.
- Added the local MiningStatistics `_Skills` Source Reader foundation: explicit
  monthly/daily source validation, one-time EOF baseline, exactly-once strict
  UTF-8 incremental lines, and durable `Truncated`/`Replaced` continuity status.
  It has no watcher, source rotation, or production registration.
- Added the local MiningStatistics session source integration: a synchronous
  reader → numeric parser → lifecycle coordinator with no history replay,
  continuity-to-Incomplete handling, terminal-batch cap behavior, and reset-safe
  pending-line handling. It has no watcher, background worker, source rotation,
  or production registration.
- The profile-panel Prospecting section is now named `Prospecting Results`.
  PlainRock Prospecting now supports MaterialOnly, Grade, and ExactMax results:
  `Plain Rock` is shown immediately when Wurm supplies no Quality, then upgrades
  when Grade or ExactMax arrives. PlainRock remains distinct from a vein: it has
  no Remaining or vein confidence and does not participate in binding, yield
  matching, or vein Remaining tracking.

### Fixed

- Hardened MS-3D1 runtime and `_Skills` reader checkpoint behavior. I/O access
  failures retry on the next tick without `Incomplete`, reset, or rebaseline;
  `Truncated`/`Replaced` remain `Incomplete`. Reader position, fingerprint, and
  pending text commit only after full read and fingerprint success, preserving
  the prior checkpoint on a pre-commit failure. Worker faults are exposed by
  `GetRuntimeSnapshot()` with the original exception and are preserved by later
  Stop/Dispose; cleanup blocks new operations and awaits the worker outside the
  coordinator gate.

- Added the real Mining-start aliases `You start to mine up.` (17,734 observed
  occurrences) and `You start to mine down.` (4,404). They use the existing
  `MiningActionContext.Wall` lifecycle of `upwards`/`downwards`, including
  MiningAttempt admission, guarded Exact Remaining decrement, and Moment of
  Inspiration correlation. No other Mining-start aliases were added.

### Deferred

- Closed MS-CORR1/MS-CORR2 Event↔Skills audit: exact action/tick pairing and
  deterministic standard-gain ownership are **BLOCKED BY SOURCE CONTRACT**;
  common Event/Skills Start/Reset/Stop boundaries are **NOT POSSIBLE WITH CURRENT
  SIGNALS**. `% actions with Mining gain` and `Standard Mining Gain / 100
  actions` are deferred/blocked by that contract. Event and Skills lack shared
  action/gain identity, cross-stream ordering, and client acknowledgement of a
  common boundary; no timestamp-window or FIFO pairing heuristic is used.
- MS-5 `TotalMiningActions` remains the correct Event-based START counter, and
  MS-6 `PositiveMiningGain` remains the correct count of all positive numeric
  Mining updates; neither is redefined by this conclusion. Reopen only with a
  newly confirmed deterministic Wurm source signal.

## [1.4.0] - 2026-09-10

### Fixed

- `Truncated` and `Replaced` Event-log generations now propagate the existing
  ordered `StreamContinuityLost` control event. The boundary does not create a
  Mining line, Mining event, material count, or `TotalCount` change.
- Startup monitoring now captures an UTF-8-safe offset and fingerprint for each
  existing active Event-log file, then catches up data appended after the
  snapshot without replaying historical complete lines. Replacement before
  initialization is detected through the captured fingerprint.
- Startup snapshot and both active-file enumerations are all-or-nothing. A
  failed snapshot or enumeration stops the watcher, detaches handlers, and
  leaves no partial state, FIFO work, or guessed EOF checkpoint.
- Fixed a real-data Moment of Inspiration undercount: `The wall will break
  soon.` now preserves pending MOI until its qualifying local primary yield.
- Clear stale per-profile Nearby Scan on Ceiling start without resetting the
  existing Vein Context, and after synchronized direct Prospecting establishes
  or updates that context.
- Added real-log support for `You would mine stone rock shards here.` as an
  existing PlainRock result, including its context-clear lifecycle.
- PlainRock now also finalizes correctly when its valid Quality is one of the
  existing Grades; its existing lifecycle clears stale prior Vein Context
  without creating a replacement. The fix was derived from real-log coverage.
- Of 7,379 audited PlainRock + Grade sequences, 7,378 are covered directly by
  this fix; the remaining overlap was subject to the separate B4 emergence-reset
  issue, now resolved separately below.
- Direct RockSalt Prospecting with Grade now finalizes through the existing
  Salt finalizer; the synchronized context preserves Grade with
  `Remaining.Unknown`. The fix was derived from 72 confirmed real-log
  sequences.
- Added the historical `You would mine salt rock salt here.` RockSalt
  Prospecting alias. It maps to the existing `VeinMaterial.RockSalt`; its
  `Grade(Poor) + Salt` flow uses the existing synchronized RockSalt lifecycle.
  The fix was derived from one confirmed real-log sequence.
- A recognized vein emergence no longer discards a fresh active Prospecting
  sequence awaiting its first direct material or PlainRock. The emergence
  TypeOnly observation is preserved and the later direct result uses the
  existing pipeline; sequences that already captured material or PlainRock
  still reset. The fix addresses 26 known complete B4 sequences from real-log
  audit coverage.
- Added real-log support for direct `You would mine gold ore here.` Prospecting
  and pure `A vein of pure gold emerges here.` vein emergence through the
  existing Gold/Ore domain behavior.
- Renamed the user-facing `Mining Inspirations` label to `Moment of
  Inspiration`.

### Changed

- Normal Event-log batches and continuity boundaries now share one FIFO
  downstream ingress. Batch and boundary handoff failures retain FIFO order and
  use the existing bounded retry policy.
- The reversible Event-log source `Stop()` operation is used only to clean up a
  failed startup; normal monitoring lifecycle is unchanged.

### Deferred

- Monthly/daily cross-file deduplication, cross-file ordering, and late
  old-tail handling after the active-window grace period remain deferred.
- Production audit of the real monthly `_Event.2026-09.txt` stream confirmed
  that one monthly file can span multiple days and midnight without a daily
  file rotation. It also confirmed legitimate identical Event lines in the
  same second; `timestamp + exact text` is therefore not a safe deduplication
  key. No corresponding daily log established a monthly/daily duplicate.

## [1.3.1] - 2026-08-25

### Added

- Added the MIT `LICENSE` file for WurmMiningCounter.
- Added license information to README and included `LICENSE` in distribution packages.

### Changed

- Added formal author, company, copyright, repository, and MIT license metadata to the application project.

## [1.3.0] - 2026-08-25

### Added

- Added in-memory Runtime Diagnostics with bounded, never-throw `Info`,
  `Warning`, and `Error` entries; Settings diagnostics controls and a non-modal
  Diagnostics window support stable chronological selection, copy, confirmed
  clear, and user-selected TXT Save Log.
- Runtime Diagnostics retain detailed local data in memory for troubleshooting;
  Copy and Save Log sanitize profile identifiers, local filesystem paths, and
  local-account data before export.

- Added independent per-profile Nearby Scan support for `There is ... nearby.`:
  ordered 1..N entries, optional quality, duplicate preservation, `Only rock`,
  latest-snapshot replacement, reset/continuity clearing, and the conditional
  `Nearby Scan` profile-panel section. Analyse remains a separate feature.
- Added passive per-profile Event-log segment-change diagnostics in the
  Prospecting sidecar. It reports old/new paths, `Monthly`/`Daily` formats, and
  `Newer`/`Older`/`Ambiguous` classification without rejecting or sorting lines
  and without emitting `StreamContinuityLost`.
- Added shared selective material reset through
  `MiningSessionCounter.ResetMaterials(profile, materials)`. It removes only
  selected current material counters without writing zero values and preserves
  Total, other materials, Mining Inspirations/pending MOI, Vein Context,
  Remaining, Nearby Scan, MiningAttempt, and Prospecting state.
- ResetWindow now lists only positive material counters with checkbox
  selection and a Select All/Clear All toggle. The main Reset action is the
  sole execution action; Reset All Material Counters remains available and is
  mutually exclusive with individual selection.
- Added the confirmed material-name context-menu shortcut
  `Reset <Material> counter`. It uses the shared reset operation, is limited to
  the material-name hit area, and does not affect other application menus.

### Changed

- Runtime Event-log monitoring now uses the per-profile active window
  `[localNow - 5 min, localNow]`. Monthly and daily segments may run together;
  expired/history segments are excluded from state, reconciliation/read, retry,
  and watcher processing. `LineRead` and Mining/Prospecting pipelines are
  unchanged; full rotation continuity remains deferred.
- Vein Context diagnostics now use
  `Profile | VeinContextUpdate | Material=... | Quality=... | Remaining=... | Confidence=...`
  with explicit `Unknown` values.
- Settings and Diagnostics are independent normal windows (`Topmost=false`)
  without a `MainWindow` owner; MainWindow Always On Top remains independent.
- Diagnostics now show Event-log segment opens using the real local profile and
  segment filename only. Copy and Save Log pseudonymize the profile while
  retaining the segment filename.
- Diagnostics table presents only Time, Level, Source, and Message; Message
  fills remaining width and rows use subtle horizontal separators only.

- Nearby Scan/Only rock and local Surface/Tunnelling starts now invalidate only
  the matching cave state: ActiveVeinContext, Vein MiningAttempt, and partial
  Prospecting. They do not change Remaining, Mining counters, Total, or Mining
  Inspirations; queue and third-person tunnelling remain ignored.

- Improved runtime reliability diagnostics through `Trace.TraceError`: isolated
  `LineRead` subscribers, Event-log read/retry/reconciliation/watcher failures,
  Settings Load/Save failures, tray async/Dispatcher failures, and startup or
  shutdown cleanup failures are reported without blocking later safe work.
- `SettingsService.Save()` now returns a success result while retaining the
  existing atomic temporary-file replacement strategy.
- Settings runtime values are now committed only after a successful save. On
  failure the existing UI status reports `Settings save failed`, Always On Top
  is reverted, and diagnostic details remain in `Trace`.
- Expanded reliability coverage for Event-log permission/I/O and enumeration
  failures, persistent read retry, startup and shutdown isolation, and tray
  initialization fallback.

- Full rotation continuity for overlapping monthly/daily segments remains
  deferred; the passive diagnostic intentionally does not decide runtime order.

- Mining UI now updates the changed profile event-driven through the
  Dispatcher after a real `MiningSessionCounter` change; the one-second timer
  remains a fallback/reconciliation path and unchanged snapshots do not cause
  a UI refresh. Live measurement confirmed that Wurm batches Event-log writes
  at roughly 10-second intervals, observed up to roughly 20 seconds, so the
  application cannot display data before Wurm physically writes it to the log.
- Full Reset All Material Counters and selective reset now share one lower
  level counter-reset mechanism. The full reset retains its prior behavior:
  it clears every material counter, preserves Total, and clears pending MOI.

### Verified

- Full suite: **946/946 PASS**.

## [1.2.0] - 2026-08-23

### Changed

- Classified Mining results centrally as primary or byproduct. Only
  StoneShards, Ore, Shards, and RockSalt increment `TotalCount`; PinkCrystals,
  Salt, Flint, SourceCrystal, Sapphire, Diamond, Emerald, Ruby, and Opal keep
  their own counters without changing Total or Remaining.
- Added exact local `You get a source crystal.` recognition as the v1
  SourceCrystal byproduct.
- Added per-profile Mining Inspirations: only confirmed
  Mining start -> MOI -> primary yield sequences increment it. MOI alone does
  not create Mining yield, Total, material, or Remaining changes; rarity tiers
  remain DATA-NEEDED.
- Added the conditional `Mining Inspirations` total row to each profile panel.

### Verified

- Full suite: **852/852 PASS**.
- Mining Inspiration integration harness: **10/10 PASS**.

## [1.1.1] - 2026-08-22

### Changed

- Unified Progressive Prospecting uses one path regardless of player skill;
  only recognized Event-log messages determine available precision:
  `Material -> TypeOnly`, `Material + Quality -> Partial`,
  `Material + Quality + Remaining -> Synchronized`, and a local Exact
  decrement -> `Tracking`.
- Added timestamp-normalized vein-emergence recognition for
  `A vein of pure <material> emerges here.`. A recognized existing
  `VeinMaterial` is applied through `ProspectingObservationUpdate ->
  ActiveVeinContext` as Material-only/TypeOnly, replacing prior Quality and
  Remaining without binding depletion or changing Mining counters or Total.
- Vein-emergence state remains isolated per `ProfileName`; the live issue where
  only the first profile received an emergence context is resolved.
- Added timestamp-normalized descriptive emergence literals for Marble and
  Slate. Sandstone and RockSalt identification remains in `VeinExamineParser`.
- `ApplyMaterialObservation` now centralizes lifecycle for Prospecting,
  emergence, and Examine: a material that contradicts a bound attempt cancels
  that attempt before replacing the context as TypeOnly; a same-material
  observation is a benign confirmation and does not degrade Exact/Synchronized/
  Tracking state.
- ResetWindow now waits for its SizeToContent layout before positioning and
  keeps its bottom edge anchored 12 px above the working-area bottom as its
  height changes; its existing MaxHeight, scrolling, width, and custom chrome
  remain unchanged.

### Verified

- Live test on three profiles and contradiction lifecycle: PASS.
- Full suite: **802/802 PASS**.

## [1.1.0] - 2026-08-22

### Changed

- Restored a stable, direct Mining Counter path:
  `EventLogMonitor.LineRead -> MiningEventMonitorBridge -> MiningEventProcessor -> MiningSessionCounter`.
- Moved Prospecting to an independent `ProspectingEventLineSidecar` with its
  own ingress queue and `EventLineCoordinator` for ordered Vein processing.
- Removed coordinator delivery from the runtime `EventLogMonitor`.
- Prospecting is isolated from the Mining Counter and cannot block its live-line
  delivery.

- Added Material-only Prospecting (`TypeOnly`), descriptive Grade qualities,
  and Estimated Remaining with six EstimateBands and presentation ranges.
- The complete `Material -> Grade -> EstimateBand` flow synchronizes
  `ActiveVeinContext`.
- Exact Remaining shows transient `↓N <Remaining>` only after an explicitly
  signalled local decrement; the series accumulates for about one second.

- Vein Context is presented inside its profile panel, with color-coded
  Confidence and the profile-panel autosize/layout refinements.
- Profile Total is now cumulative and independent from the current material
  counters.
- Reset Session remains global and is the only operation that resets Total.
- Added per-profile Reset All Material Counters, preserving Total and Vein
  Context; individual-material reset is deliberately not supported.
- Added ordered per-profile Reset Vein Context, preserving counters and Total.
- Tray now provides `Reset...` with checkbox selection, confirmation, dynamic
  height, Settings-style positioning, and custom chrome.

### Verified

- Live test confirmed that the Mining Counter and Vein Context operate concurrently.

- Live: Material-only, Estimated, and Exact with delta indicator PASS; no Mining
  Counter regression.
- Full suite: 775/775 PASS.

### Open / Deferred

- Rotation continuity after monthly/daily overlap and late old-tail remains
  OPEN / DEFERRED in the sidecar; it must not be restored in
  `EventLogMonitor`.

## [1.0.1] — 2026-08-20

**Status: RELEASED.** GitHub Release: [v1.0.1](https://github.com/Projekt-Next/WurmMiningCounter/releases/tag/v1.0.1) in the private `Projekt-Next/WurmMiningCounter` repository for Windows x64. Tag `v1.0.1` points to commit `20999bfcb010dea3e27fffeb3aaf6abfc8020ada`.

### Changed

- MainWindow and SettingsWindow use borderless chrome with custom drag regions.
- SettingsWindow has a custom `×` button, opens at the bottom-right of the
  `WorkingArea`, and displays the dynamic version without build metadata.
- Profile headers show `TotalCount`; section and material-counter layout was
  improved for compact window widths.
- A single left-click on the tray icon toggles Show / Hide; a minimized window
  is restored.

### Tested

- 259/259 tests PASS.
- Release build: 0 warnings, 0 errors.
- The release contains five assets: Standard, Compressed, Lite,
  `SHA256SUMS.txt`, and `README.txt`.

## [1.0.0] — 2026-08-19

**Status: RELEASED.** GitHub Release: [v1.0.0](https://github.com/Projekt-Next/WurmMiningCounter/releases/tag/v1.0.0) in the private `Projekt-Next/WurmMiningCounter` repository for Windows x64.

### Added

- Global monitoring of Wurm Online Event logs for multiple profiles.
- Per-profile session counters for 12 Mining materials.
- Tray controls for Show / Hide, Always On Top, Settings, and Exit.
- Settings auto-save for Always On Top and Start Minimized To Tray.
- Reset Session with confirmation in Settings.
- Saved and safely restored window position and width.
- Single-instance behavior with a mutex and `ACTIVATE` pipe.
- Final `Resources/WurmMiningCounter.ico` icon for the EXE, MainWindow, and tray.

### Changed

- Recognized the actual `_Event.YYYY-MM.txt` and `_Event.YYYY-MM-DD.txt` filenames.
- Monitoring of existing logs starts at EOF, without historical backfill.
- Profiles are shown only after the first recognized Mining event.
- MainWindow has a compact appearance, dynamic height, and vertical scrolling
  when content exceeds the available space.

### Fixed

- Reconciliation, retries, partial lines, strict UTF-8, Event-file truncation,
  and replacement handling.
- A secondary instance exits before creating services and activates the primary.

### Tested

- 256/256 tests PASS.
- Release build: 0 warnings, 0 errors.
- Standard manual live test: PASS.
- Lite/Compressed publication tests: PASS WITH NOTES.
- No runtime errors were detected.
