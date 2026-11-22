# Roadmap for WurmMiningCounter 1.9.0

This document describes work that remains **OPEN**, **DATA-NEEDED**, **DEFERRED**, or **FUTURE** for WurmMiningCounter 1.9.0. It does not replace the release history in `CHANGELOG.md`.

Details of implemented features belong in [Architecture](ARCHITECTURE.md) and the [Changelog](../CHANGELOG.md). Prospecting domain rules are maintained in the [Prospecting Domain Baseline](PROSPECTING_DOMAIN_BASELINE.md), with concrete behavior in the [Prospecting Specification](PROSPECTING_SPECIFICATION.md) and [Prospecting Scenarios](PROSPECTING_SCENARIOS.md).

## Delivered baseline

Release 1.8.2 includes canonical Source Crystals counting, stable Reset menu context across profile-panel rebuilds, an independent per-profile Moment of Inspiration reset, and a shared Reset Prospecting Results action with context clearing on cave entry and exit. The earlier v1.8.0 baseline includes the current Event Pipeline, Event Alerts, progressive Prospecting and Vein Context, Nearby Scan, Mining Inspirations, selective material reset / Quick Reset, and automatic live Skill Statistics through one application-lifetime `_Skills` path, Mining Session Statistics, and Pickaxe Sweet Spot guidance.

Their history is not repeated here; their current architecture and change history are described in the documents linked above.

## Live Detection / Log Discovery

**DELIVERED — v1.8.3:** one versioned discovery snapshot is used at startup
and by Settings > Find locations. Default Wurm Online Standard and Steam plus
Wurm Unlimited Steam rules catalog configured log sources; additional locations
can be added manually. Refresh adds newly discovered sources without restarting
existing monitors. Complete-snapshot-only retirement uses confirmed physical
directory identity, with Event FIFO-drain-gated reappearance and fresh Skills
writer declaration. Incomplete discovery remains add-only. This discovers log
locations, not a running game process. See [Architecture](ARCHITECTURE.md) for
the implemented lifecycle and limitations.

## Skill Statistics

**DELIVERED — v1.8.0 automatic live Skill Statistics:** the application has one application-lifetime `LiveSkillMonitor` for all profiles. Historical `_Skills` data is used only to bootstrap Current Mining; live updates supply Current, Last Gain, Session Gain, and raw `PositiveUpdateCount` for all numeric skills. MainWindow and `SkillStatisticsWindow` use the same immutable snapshots.

Mining Statistics also shows Successful actions, Mining ticks, Tick rate, Actions / tick, Avg gain / tick, Average ticks / hour, Avg ticks / N actions, observed gain/hour, the strict next milestone (0.50 below 99.00, 0.01 from 99.00 to 100.00), and ETA. Derived Mining Session Statistics use a per-profile baseline established at startup or by Reset Session; raw `PositiveUpdateCount` and raw `SessionGain` remain application-lifetime. There is no manual Start/Stop/New Session, source selection, dump workflow, old session runtime, or separate `LiveMiningMonitor`.

**CLOSED — Event↔Skills pairing:** exact action/tick pairing and deterministic standard-gain ownership remain **BLOCKED BY SOURCE CONTRACT**. Event and Skills have no shared identity, cross-stream sequence/order contract, or client acknowledgement of a shared boundary. No timestamp-window or FIFO-pairing heuristics are used. This may be revisited only if a new, confirmed deterministic source signal becomes available.

## Diagnostic evidence collection

**CLOSED / DEFERRED — audits 79A–79C:** discovery and audit of real local
`_Event` / `_Skills` data were completed. They confirmed that more detailed
Evidence Capture is technically feasible for further study, but found no new
real diagnostic problem that requires a feature.

Decision: the current `RuntimeDiagnosticService` is sufficient for present
needs. `EvidenceCaptureSession`, `_Skills` tailing, Event↔Skills pairing, and
expanded Domain Diagnostics are not being implemented. Findings from 79A–79C
are audit evidence, not an implementation plan. The topic may be reopened only
in response to a new real diagnostic problem.

## Prospecting / Vein tracking — open work

### ExternalDepletion

**OPEN:** a separate audit of the model and its diagnostic use is required.

Frozen constraints:
- `ExternalDepletion` means a difference between locally predicted Remaining and a later authoritative world observation;
- it is not attributed to a specific player;
- it does not change historical Mining counters, material counters, or `TotalCount`;
- it cannot by itself prove the identity of a physical vein.

Open questions include diagnostic storage and presentation, and the conditions under which a difference can safely be called ExternalDepletion.

### Quality / Remaining continuity contradictions

**DATA-NEEDED:** safe contradiction predicates are needed for continued continuity of the same vein.

A new complete Prospecting remains an authoritative observation and may replace the previous context; it is not itself a continuity error. These open predicates do not block the implemented Exact/Grade/Estimate flows or guarded decrements.

### Direct Prospecting literals and parser evidence

- **DELIVERED — real-data Gold:** `You would mine gold ore here.` (75 ProfileA occurrences) is parsed through the existing direct Prospecting pipeline, while `A vein of pure gold emerges here.` (16 occurrences) resolves through the existing pure-vein material lookup. Both use existing Gold/Ore semantics.
- **DATA-NEEDED — Additional Estimated texts:** the six currently supported estimate bands remain implemented; only additional, unconfirmed text variants are open.
- **DATA-NEEDED — Remaining evidence provenance:** the currently replayable
  dataset is 129 `_Event` files from 2015–2018. Audit 77A confirms five
  estimate literals there (`FewWeeks`, `StartingToDeplete`,
  `ProspectAnotherVein`, `RunOutSoon`, `RunOutAnyHour`), while Exact Remaining
  and `Plenty` each occur zero times. Exact Remaining remains **HISTORICALLY
  CONFIRMED** from the broader 62A corpus, but its raw logs are unavailable for
  recount/replay; `Plenty` remains **DATA-NEEDED**. This does not justify a
  runtime change.
- **VALIDATION-NEEDED — Exact Quality threshold:** canonical project contract is `Grade` through 90 (`20–59` and `60–90`) and numeric `ExactMax` above 90. Audit 72A observed MaterialOnly at `1.123–19.139` and Grade at `33.339–61.073`; **DATA-NEEDED** evidence is missing for `20–33.339`, `80+`, `90+`, `91+`, and ExactMax pairing. Exact thresholds therefore remain validation-needed; the parser remains literal-driven and has no skill-based branching.

Silver, Gold, Slate, and RockSalt have confirmed direct Prospecting literals and are not DATA-NEEDED items.

### Result finalization / augments

- **OPEN / DATA-NEEDED — low-precision or non-Salt RockSalt:** audit 76A
  confirms 83 direct RockSalt observations, all `Material + Quality + Salt`:
  72 `Grade(VeryGood)`, 10 `ExactMax`, and one historical alias with
  `Grade(Poor)`. There are zero confirmed MaterialOnly, Quality-without-Salt,
  or Salt-without-Quality cases. Their finalization remains unresolved; no
  rule or skill threshold is inferred. The implemented direct-Salt variant is
  unchanged.
- **DELIVERED — B1, PlainRock + Grade finalization:** existing PlainRock followed by valid Quality (`ExactMax` or any of the six existing Grades) now finalizes terminal `ProspectingResult.PlainRock`, clears a prior `ActiveVeinContext`, and creates no replacement context. Real-log audit found 7,379 PlainRock + Grade sequences; 7,378 work through this fix, including 2,822 of 2,823 cases with a stale nonempty prior context.
- **DELIVERED — MaterialOnly PlainRock:** recognized PlainRock without supplied
  Quality is now a valid result. Audit 71A confirmed 74 unique real actions;
  MaterialOnly clears a prior `ActiveVeinContext`, presents `Plain Rock`, and
  upgrades when later Grade or ExactMax is supplied. Audit 72A observes a
  lower-skill MaterialOnly correlation (`1.123–19.139`) and Grade at
  `33.339–61.073`, but cannot establish thresholds or ExactMax pairing; this
  remains VALIDATION-NEEDED and is not runtime logic.
- **DELIVERED — MaterialOnly Vein:** audit 73A confirmed 13 real
  `Prospecting start → Material → action boundary` sequences without Quality or
  Remaining. Material is a complete lower-precision observation: runtime keeps
  `ActiveVeinContext(TypeOnly)`, hides Unknown Quality/Remaining in UI, and
  leaves it unbound and outside Exact Remaining decrement. It is distinct from
  an assembler that has not yet received Material; later precision is optional.
- **DELIVERED — B2, RockSalt + Grade + Salt finalization:** direct Salt now finalizes RockSalt with preserved Grade and `Remaining.Unknown`. The fix covers 72 confirmed ProfileA sequences, all observed as `Grade(VeryGood)`; audit 76A separately confirms ten `ExactMax + Salt` observations. Generic Grade support does not assert that other Grades occurred in this flow.
- **DELIVERED — B3, historical `salt rock salt` alias:** exact `You would mine salt rock salt here.` maps to existing `VeinMaterial.RockSalt`. One confirmed ProfileA sequence (2016-12-09) then uses the existing `Grade(Poor) → Salt` synchronized RockSalt lifecycle; no generalized salt parsing was added.
- **DELIVERED — B4, emergence inside fresh Prospecting:** recognized emergence now preserves only a started assembler still awaiting its first direct Material or PlainRock, while retaining its TypeOnly observation. Later direct results use the existing lifecycle; emergence after captured Material/PlainRock still resets. Audit 62A identified 25 complete sequences plus one PlainRock + Grade overlap; archived logs are unavailable for post-fix replay.
- **DELIVERED — contextual Salt/Flint augments:** Known-Quality PlainRock and
  Nearby/OnlyRock snapshots accept contiguous exact Salt/Flint; a finalized
  normal Direct Vein with ExactMax and known Remaining accepts contiguous Salt.
  Ownership is per profile in physical Event-line order; the first non-augment
  line and all continuity/reset boundaries close eligibility. Replacements are
  immutable, duplicates are no-ops, and no timeout or timestamp heuristic is
  used. Direct Flint and RockSalt Flint remain **DATA-NEEDED**.

### Boundary / destroyed-context representation

Operational boundary behavior is implemented; only the representation of some states remains open:

- **OPEN / DATA-NEEDED:** representation after vein destruction — removed context versus a diagnostic Ambiguous/destroyed state;
- **OPEN / DATA-NEEDED:** Unknown versus Ambiguous presentation after reconnect;
- **OPEN / DATA-NEEDED:** Unknown versus Ambiguous presentation after `Logging started`;
- **OPEN / DATA-NEEDED:** UI/diagnostic representation for `Exact(0)`; operationally, Exact(0) remains Tracking, does not bind a new Wall attempt, and cannot decrement below zero.

### Empirical multiplayer / vein-identity questions

- **DATA-NEEDED:** whether the local Event log contains an unambiguous message when another player destroys a vein;
- **DATA-NEEDED:** whether `The wall will break soon.` can appear locally for another player's action;
- **OPEN / DATA-NEEDED:** a heuristic for distinguishing two physical veins of the same material. The Event log provides no stable tile/vein ID, so identity must not be inferred from material alone.

## Nearby Scan

The Nearby Scan feature itself is **IMPLEMENTED** and remains independent per profile.
Nearby and Nearby only-rock snapshots also support contiguous exact
Salt/Flint immutable augment replacements. The ownership window closes on the
first non-augment Event line and all reset/continuity boundaries; this changes
neither Mining counters nor current UI presentation.
The stale surface snapshot is cleared after a Ceiling start and after
synchronized direct Prospecting; Ceiling preserves the wall Vein Context. A
Prospecting start, an assembler awaiting its first Material, and a complete
MaterialOnly Vein do not by themselves clear Nearby.

**OPEN:** possible semantic use of parsed `MaterialText` / `GradeText`, including mapping to `VeinMaterial` / the Quality model. Do not change current snapshot behavior or cave-context invalidation without a separate decision.

Nearby Scan remains separate from Analyse.

## Analyse

**IMPLEMENTED:** confirmed shard and ore boundaries, trace and no-trace
observations, per-profile sequence assembly, Context consumer and results UI.
Analyse remains separate from direct Prospecting and Nearby Scan.

**DATA-NEEDED:** additional trace variants, materials and no-trace
wordings, and interruption/cancellation messages; see the domain baseline.

Analyse does not block Mining counting, Prospecting tracking, or Nearby Scan.

## Moment of Inspiration

User-facing `Moment of Inspiration` is **IMPLEMENTED** for the strict correlation
recognized local Mining/Tunnelling start -> MOI -> qualifying local primary.
The confirmed real-data undercount for `MOI -> The wall will break
soon. -> primary` has been fixed; real-log replay counted ProfileA 5, ProfileB 2,
and ProfileC 0. `The wall breaks!` was not added without a real example. `You see
dark dungeons.` is confirmed same-action information for Vein/MiningAttempt,
but MOI transparency remains **NOT OBSERVED** (no such sequence among 17
current MOI observations), so the tracker was not changed.

**DATA-NEEDED:** classification of Rare / Supreme / Fantastic and the exact rarity semantics. MOI alone cannot currently be interpreted as a specific rarity tier.

## Event-log continuity

**DEFERRED:** full rotation continuity and deduplication for coexisting monthly/daily segments and late old-tail.

The current active segment window `[localNow - 5 min, localNow]` limits runtime monitoring and history cost, but does not establish full continuity.

Reason for deferral:
- monthly and daily files may coexist within the same month;
- filenames alone do not provide a safe monotonic order for active segments;
- a naive merge/deduplication could disturb ordering again or deliver stale data to the current runtime.

This work belongs in a separate continuity layer. `EventPipeline` propagates
in-file `Truncated`/`Replaced` as ordered `StreamContinuityLost`, but does not
recover cross-segment historical ordering, merge files, or deduplicate monthly/daily overlap or late old-tail.

**REAL LOG CONFIRMED (2026-09):** a single monthly
`_Event.2026-09.txt` spans multiple days and crosses midnight without daily
rotation in this client configuration. It also contains legitimate identical
Event lines in the same second, so `timestamp + exact text` is not a safe
deduplication key.

- monthly/daily duplicate: **DATA-NEEDED** — no corresponding daily log is
  available for the same profile and time;
- cross-file ordering: **REACHABLE, IMPACT NOT CONFIRMED** — the monitor does not merge
  physical segments, but no real overlap/reordering has been observed;
- late old-tail after >5 min: **INTENTIONAL CURRENT POLICY / DATA-NEEDED** —
  segments outside the grace period are intentionally ignored, and there is no
  real evidence that Wurm continues writing to them.

There is no currently confirmed Event-log continuity issue requiring
implementation.

## UI / presentation — future extensions

- **FUTURE:** further development of `ActiveVeinContext` presentation and interaction. The basic presentation, Confidence, and live updates are implemented.
- **FUTURE / optional:** explicit Save/Resume Session with stale-state validation. It is not planned for the next milestone.

## Technical / optional

- **FUTURE / optional:** additional Single Instance listener diagnostics if a real diagnostic need arises.

## Out of scope

**FUTURE:** Windows autostart is not implemented.
