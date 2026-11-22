# Prospecting / Mining Domain Baseline

## Purpose and source hierarchy

**Baseline status: CURRENT for WurmMiningCounter 1.8.4.** The baseline is
complete for the known evidence, frozen domain decisions, implemented domain
semantics, and explicitly recorded open questions. `CURRENT` does not mean every
open domain question has been resolved. Historical implementation audits and
checkpoints are preserved separately under `Documentation/Archive`.

This document is the canonical domain baseline for Prospecting and vein-aware
Mining work. It records what Wurm has actually emitted, what the project has
consciously decided those observations mean, and which questions remain unsafe
to infer.

Use source precedence according to the kind of claim:

**Empirical facts and exact literals**

1. real anonymized Event-log evidence;
2. fixtures/tests that reproduce that evidence;
3. documentation summaries.

**Program/domain semantics**

1. explicit **FROZEN DECISION**;
2. current `ARCHITECTURE.md` / `PROSPECTING_SPECIFICATION.md`;
3. code and tests as evidence of what is currently implemented.

A new real log may confirm or contradict an empirical assumption, but it does
not silently change program semantics. A conflicting observation requires a
conscious domain review and baseline update. Conversely, code/tests prove
implementation state but do not silently override a frozen domain decision.

Status vocabulary used by active documentation:

- **LOG-CONFIRMED** — confirmed directly by real anonymized Event-log evidence.
- **EXTERNAL-DOCUMENTED** — documented by an external source such as Wurmpedia;
  useful evidence, but not equivalent to a real local log.
- **FROZEN DECISION** — consciously accepted project/domain semantics.
- **IMPLEMENTED** — current code and tests implement the stated behavior.
- **OPEN** — a product/domain decision has not been selected.
- **DATA-NEEDED** — more real evidence is required before a safe conclusion.
- **VALIDATION-NEEDED** — an implemented/selected hypothesis needs focused
  empirical validation.
- **DEFERRED** — known work intentionally postponed.
- **FUTURE** — idea outside the current scope.

Combined labels such as **LOG-CONFIRMED / IMPLEMENTED** are allowed when they
state two different facts about the same rule.

## Ordered Event-stream foundation

- **FROZEN DECISION:** physical source-line order is authoritative. A
  `[HH:mm:ss]` timestamp never establishes order.
- **FROZEN DECISION:** all Prospecting, MiningAttempt, and vein state is
  independent per `ProfileName`.
- **FROZEN DECISION:** `ResetProfileBarrier` and `StreamContinuityLost` are
  ordered controls in the same per-profile stream as normal Event lines.
- **FROZEN DECISION:** normal monthly rotation and a fully recovered
  replacement/truncation are not continuity boundaries when complete ordered
  recovery is proven. A real continuity/order loss is a boundary.
- **FROZEN DECISION:** a consumer failure must not stop Mining or another
  consumer; the faulted consumer/profile is invalidated.

## Prospecting sequence and direct results

- **LOG-CONFIRMED:** the only hard Prospecting start is:

  ```text
  You start to gather fragments of the rock.
  ```

- **LOG-CONFIRMED:** the queue message below is not a Prospecting start:

  ```text
  After you finish mining you will start prospecting.
  ```

- **FROZEN DECISION:** a new hard start abandons the incomplete
  `ProspectingSequence` of that profile.
- **FROZEN DECISION:** irrelevant lines, including other-player noise, between
  expected fragments do not terminate the sequence and no timeout is used.
- **LOG-CONFIRMED:** the exact-result shape is:

  ```text
  You would mine <material> here.
  It has a max quality of <quality>.
  You will be able to mine here <remaining> more times.
  ```

- **FROZEN DECISION:** a complete direct Prospecting result synchronizes and
  replaces the active context for that profile. It never changes personal
  Mining counters.
- **LOG-CONFIRMED:** `You would mine stone shards here.` is a PlainRock direct
  Prospecting result, distinct from Mining `You mine some stone shards.`.
- **LOG-CONFIRMED:** another PlainRock sequence is:

  ```text
  You start to gather fragments of the rock.
  You find only rock.
  It has a max quality of <value>.
  You will find salt here!
  ```

- **IMPLEMENTED:** `You find only rock.` is the second implemented PlainRock literal,
  covered by parser tests. Its `ExactMax` Quality finalizes
  `ProspectingResult.PlainRock(quality, empty Augments)`, with no Remaining and
  no `ActiveVeinContext`. A contiguous exact Salt/Flint tail may immutably
  replace that known-Quality result with ordered Augments.
- **LOG-CONFIRMED:** confirmed PlainRock sequences continue directly with:

  ```text
  It has a max quality of <value>.
  ```

  PlainRock plus `ExactMax` Quality is therefore a confirmed sequence. Real
  coverage also confirms PlainRock followed by each supported `Grade`; both
  Quality forms have no Remaining line. Observed examples then continue with,
  among other things, a new Prospecting start or a Mining start.
- **FROZEN DECISION:** PlainRock closes any previous vein context but creates
  neither a `VeinMaterial`, `YieldKind`, nor a new `ActiveVeinContext`.
- **LOG-CONFIRMED / IMPLEMENTED:** audit 71A found 74 unique real Prospecting
  actions with `start -> recognized PlainRock literal -> no Quality ->
  unambiguous next action boundary`. MaterialOnly is therefore a valid
  `ProspectingResult.PlainRock(Unknown, empty Augments)`: it clears a previous
  vein context and is immediately available for presentation. A later Grade or
  ExactMax is an optional precision upgrade of the same PlainRock result. The
  observed lower-skill association is evidence only; no skill rule or threshold
  belongs to the pipeline.
- **LOG-CONFIRMED:** two active Prospecting sequences contain:

  ```text
  You start to gather fragments of the rock.
  You see a ceiling.
  <perimeter/noise>
  You would mine stone stone shards here.
  It has a max quality of 99.
  You will find salt here!
  ```

  The same `You would mine stone stone shards here.` literal occurs twice.
  Noise before it does not interrupt Prospecting, and neither sequence contains
  a Remaining line.
- **IMPLEMENTED:** `You would mine stone stone shards here.` is the third implemented
  exact PlainRock literal, covered by parser tests. Its `ExactMax` Quality finalizes
  `ProspectingResult.PlainRock(quality, empty Augments)`, with no Remaining and
  no `ActiveVeinContext`; a contiguous Salt/Flint tail can update Augments.
  This literal must not map to `VeinMaterial.Shards`.
- **LOG-CONFIRMED / IMPLEMENTED:** `You would mine stone rock shards here.` occurs
  5,861 times in ProfileA real Event logs. In the confirmed
  `Prospecting start → stone rock shards → ExactMax` flow it is PlainRock, not a
  `VeinMaterial`: it finalizes `ProspectingResult.PlainRock`, clears any previous
  `ActiveVeinContext`, and creates no new vein context or Remaining.
- **LOG-CONFIRMED:** `You will find salt here!` and `You will find flint here!`
  are direct Prospecting augment messages. The Salt augment directly following
  ExactMax Quality in the confirmed RockSalt-without-Remaining sequence is also
  confirmed.
- **LOG-CONFIRMED:** Salt also occurs after a standard exact Prospecting result:
  Material → Quality → Remaining → Salt.
- **LOG-CONFIRMED:** Salt and Flint also occur after NearbyScan:
  NearbyScan → Salt → Flint.
- **FROZEN DECISION:** contextual Augments apply only to known-Quality PlainRock,
  Nearby/OnlyRock, and qualified normal Direct Salt results. They are not
  independent vein results, do not create a PlainRock context, decrement
  Remaining, or modify Mining counters. RockSalt Salt remains a separate direct
  finalizer, not a contextual Augment. These messages differ from Mining
  `You mine some salt.` and `You find flint!`.
- **IMPLEMENTED:** association is a contiguous physical Event-line tail per
  profile: known-Quality PlainRock and Nearby/OnlyRock accept Salt/Flint, and
  a finalized normal Direct Vein with ExactMax plus known Remaining accepts
  Salt. The first non-augment line and every hard/reset/continuity boundary
  closes ownership. Direct Flint and RockSalt Flint remain **DATA-NEEDED**.
- **LOG-CONFIRMED:** ten RockSalt observations have the ordered prefix
  `RockSalt → ExactMax Quality → Salt`; none contains a Remaining line. After
  Salt, real observations include both a new Prospecting start and an actual
  Wall Mining start. Salt also occurs after other Prospecting results.
- **LOG-CONFIRMED:** one observed RockSalt sequence continues as:

  ```text
  You start to gather fragments of the rock.
  You would mine rock salt here.
  It has a max quality of 74.
  You will find salt here!
  <queue message(s)>
  You start to mine forwards.
  You mine some rock salt.
  ```

  It contains no Remaining line. Queue messages are not finalizers. The actual
  Wall Mining start `You start to mine forwards.` is a confirmed next boundary
  candidate, followed by `You mine some rock salt.`.
  RockSalt plus `ExactMax` without Remaining, and direct Salt after Quality,
  are therefore confirmed.
- **FROZEN DECISION:** direct RockSalt synchronizes a RockSalt context; absent
  Remaining is not a sequence error and leaves Remaining Unknown.
- **LOG-CONFIRMED:** `You stop prospecting.` occurs as an exact Event-log
  literal.
- **FROZEN DECISION:** `You stop prospecting.` terminates only the current
  partial Prospecting sequence; it does not by itself clear an already
  synchronized vein context.
- **LOG-CONFIRMED / IMPLEMENTED:** Nearby Scan has outer shape `There is ... nearby.`. Real,
  anonymized Event-log examples are:

  ```text
  There is tin (poor quality) nearby.
  There is iron (normal quality) and iron (good quality) nearby.
  There is iron (normal quality), iron (good quality), iron (poor quality) and sandstone (normal quality) nearby.
  There is iron (normal quality), silver (acceptable quality) and iron (very good quality) nearby.
  There is rock salt (good quality), tin (acceptable quality), rock salt (very good quality), sandstone (poor quality), iron (good quality) and sandstone (very good quality) nearby.
  ```

- **FROZEN DECISION:** Nearby Scan uses exact prefix `There is ` and
  suffix ` nearby.`. It parses one or more ordered entries separated by commas
  and a final `and`; each entry is `<material text>` with optional
  `(<grade> quality)`. `NearbyProspectingObservation` preserves
  `RawDescription`, ordered `Entries`, and `IsOnlyRock`; each entry preserves
  `MaterialText` and optional `GradeText`. Entries are neither mapped to
  `VeinMaterial`/`QualityMeasurement` nor deduplicated.
- **LOG-CONFIRMED / IMPLEMENTED:** `There is only rock nearby.` is a Nearby
  Scan with `IsOnlyRock = true` and no entries. The same material may occur
  multiple times, including at different grades; multi-word material text such
  as `rock salt` is preserved.
- **FROZEN DECISION / IMPLEMENTED:** Nearby Scan is a separate surface scan.
  The latest valid snapshot replaces the prior snapshot only for the same
  `ProfileName`; it is cleared by Reset Session, Reset Profile/Vein Context,
  reconnect, `Logging started`, and `StreamContinuityLost`. A Nearby Scan or
  `There is only rock nearby.` is also a hard cave-state invalidation: it clears
  `ActiveVeinContext`, the Vein `MiningAttempt`, and partial Prospecting. It
  does not decrement Remaining or change Mining counters, Total, or Mining
  Inspiration.
- **FROZEN DECISION / IMPLEMENTED:** a stale Nearby snapshot is cleared only
  for the same profile when `You start to mine the ceiling.` starts the unbound
  Ceiling attempt, or after a synchronized direct Prospecting observation has
  established/updated `ActiveVeinContext`. Prospecting start alone and an
  assembler that has not yet received Material do not clear Nearby. A complete
  MaterialOnly Vein observation also leaves Nearby unchanged under the current
  synchronized-only clear policy. These transitions do not reset Vein Context,
  Remaining, Mining counters, Total, or Moment of Inspiration.
- **IMPLEMENTED:** each Nearby or OnlyRock snapshot may receive a contiguous
  exact Salt/Flint tail as an immutable Augments replacement. Eligibility is
  per profile and separate from the presentation snapshot; the first
  non-augment line and all reset/continuity boundaries close it. Duplicates are
  no-ops in first-seen order, and current UI does not display Augments.
- **FROZEN DECISION:** the MainWindow renders `Nearby Scan` after Vein
  Context only when the profile has a snapshot. Ordered entries are displayed
  as received; `IsOnlyRock` renders `Only rock`. A profile with only a Nearby
  Scan remains visible.
- **OPEN / DATA-NEEDED:** the complete material list and quality vocabulary
  remain observational; no semantic mapping to `VeinMaterial` or
  `QualityMeasurement.Grade` is inferred.
- **FROZEN DECISION:** Nearby Scan is a Prospecting result. Analyse remains a
  separate feature: Analyse trace literals are not Nearby Scan input and do not
  populate its snapshot.
- **LOG-CONFIRMED:** Analyse boundaries observed in real Event logs are
  `You start to analyse the shard.`, `You finish analysing the shard.`,
  `You start to analyse the ore.`, and `You finish analysing the ore.`. The
  current `AnalyseLineParser` supports all four exact boundaries.
- **LOG-CONFIRMED:** an item-description line can follow Analyse start. Observed
  examples are `Lots of different sized stone shards.` for a shard and
  `A reddish chunk of rock containing many cavities lined with dark, metallic
  crystals.` for ore. Such a description is not an `AnalyseTrace`, does not
  identify an `ActiveVeinContext`, and has no frozen complete variant list.

### Analyse: real-log evidence, external documentation, and open scope

- **EXTERNAL-DOCUMENTED:** the
  [Wurmpedia Analyse page](https://www.wurmpedia.com/index.php/Analyse)
  documents this possible Analyse readout:

  ```text
  You start to analyse the shard.
  Lots of different sized stone shards.
  You notice a trace of silver ore (northeast).
  You take a closer look.
  You notice a slight trace of iron ore (south of west).
  You finish analysing the shard.
  ```

  This source remains **EXTERNAL-DOCUMENTED** evidence. The real-log audit below assigns
  `LOG-CONFIRMED` only to separately observed literals and shapes.

- **LOG-CONFIRMED:** all six Analyse strength literals occur in real logs:
  `trace`, `slight trace`, `faint trace`, `minuscule trace`, `vague trace`, and
  `indistinct trace`.
- **EXTERNAL-DOCUMENTED:** [Wurmpedia Analyse](https://www.wurmpedia.com/index.php/Analyse)
  maps those strengths, respectively, to distances 1, 2, 3, 4, 5, and 6 tiles.
  Each distance is a square zone surrounding the preceding zone, centered on
  the tile where the shard was mined. The log reports strength, not distance.
  The UI presents the corresponding 1–6 tiles as an approximate distance ring;
  this does not establish the exact tile of a deposit.
- **LOG-CONFIRMED:** observed structural stage markers are non-trace lines:

  ```text
  trace              → You take a closer look.
  slight trace       → You study it a bit more.
  faint trace        → You study it real hard.
  minuscule trace    → You peer at it.
  vague trace        → You go cross-eyed studying it.
  indistinct trace   → Analyse finish follows.
  ```

- **LOG-CONFIRMED:** known-material trace lines use observed verbs `You notice`,
  `You see`, and `You spot`, with observed shape:

  ```text
  You <verb> a <strength> of <grade> quality <material> (<direction>).
  ```

  Real examples include:

  ```text
  You notice a trace of utmost quality slate (east).
  You see a slight trace of acceptable quality iron (east).
  You spot a faint trace of utmost quality copper (east of south).
  You notice a minuscule trace of poor quality sandstone (north of east).
  You see a vague trace of very good quality gold (west of south).
  You notice an indistinct trace of normal quality rock salt (north of east).
  ```

  This is Analyse grammar, distinct from direct Prospecting grammar. Direct Gold
  Prospecting is independently confirmed below.

- **LOG-CONFIRMED:** observed grade labels are `poor`, `acceptable`, `normal`,
  `good`, `very good`, and `utmost`.
- **EXTERNAL-DOCUMENTED:** Wurmpedia gives these quality ranges: Poor 20–29 QL,
  Acceptable 30–39 QL, Normal 40–59 QL, Good 60–79 QL, Very good 80–94 QL,
  and Utmost 95–99 QL. These ranges are reference information; neither they
  nor the observed labels authorize a mapping to `QualityMeasurement.Grade`.
- **LOG-CONFIRMED:** Salt and Flint can be Analyse observation targets without
  a quality grade: `You notice a faint trace of salt (north).` and
  `You see a vague trace of flint (northwest).` They are not direct
  `VeinMaterial` results and are not `ProspectingAugment` values in this
  context.
- **LOG-CONFIRMED:** unidentified material has observed shape
  `You <verb> an <strength> of something, but cannot quite make it out
  (<direction>).`, with notice, see, and spot variants. Example:
  `You spot an indistinct trace of something, but cannot quite make it out
  (south of east).` LIVE-A1 also confirmed `You spot a vague trace of something,
  but cannot quite make it out (south of west).` Unidentified material is
  represented without adding `VeinMaterial.Unknown`.
- **LOG-CONFIRMED:** observed Analyse material texts are iron, copper, tin,
  zinc, lead, silver, gold, sandstone, marble, slate, rock salt, salt, and
  flint. This is an observed list, not a complete declaration of possible
  Analyse materials.
- **LOG-CONFIRMED:** real logs contain parenthesized direction values across
  cardinal, diagonal, and intermediate families: `north`, `south`, `east`,
  `west`; `northeast`, `northwest`, `southeast`, `southwest`; `east of north`,
  `west of north`, `north of east`, `south of east`, `east of south`,
  `west of south`, `north of west`, and `south of west`. Do not create a
  Direction enum yet.
- **EXTERNAL-DOCUMENTED:** Wurmpedia says direction requires a compass in the
  inventory. Its example discussion on [Talk:Analyse](https://www.wurmpedia.com/index.php/Talk:Analyse)
  includes trace lines without direction. **LOCAL-LIVE:** directionless traces
  were observed on a character without a compass; do not infer compass possession
  from an individual line.
- **EXTERNAL-DOCUMENTED:** Wurmpedia says multiple veins can be reported in
  the same distance zone, including multiple veins of the same material on
  separate lines. Talk:Analyse includes multiple trace lines in one stage.
- **EXTERNAL-DOCUMENTED:** a trace of `something` that cannot be identified
  reports an observation with unknown material, not an absence of traces.
  This differs from the explicit no-trace messages below.
- **EXTERNAL-DOCUMENTED:** Prospecting skill affects which trace strengths,
  materials, and quality details can be identified. The main page and its
  talk page contain incomplete or conflicting thresholds; the talk page
  describes historical observations, a pre-14 April 2017 table and a later
  claimed change, and a Wurm Unlimited observation needing Wurm Online
  confirmation. Treat thresholds and historical examples as reference, not
  current verified game rules or runtime eligibility rules.
- **EXTERNAL-DOCUMENTED:** Wurmpedia lists these Analyse refusal messages:

  ```text
  You are unable to work out how to analyse the shard.
  It looks like someone has tampered with the stone shards and therefore you decide not to analyse it.
  The stone shards looks too old for a decent analysis and therefore you decide not to analyse it.
  ```

  The page associates them respectively with insufficient Prospecting,
  tampered or previously analysed shards, and shards from bulk storage.
  **DATA-NEEDED:** locally observed literals and their conditions; these
  external examples do not establish interruption or sequence boundaries.
- **LOG-CONFIRMED:** exact observed no-trace variants include:

  ```text
  You do not notice any unusual traces.
  You cannot see any unusual traces of anything.
  You do not notice any unusual slight traces.
  You cannot see any unusual slight traces of anything.
  You do not notice any unusual faint traces.
  You cannot see any unusual faint traces of anything.
  You cannot see anything unusual.
  You do not notice any unusual minuscule traces.
  You do not notice any unusual vague traces.
  You cannot see any unusual vague traces of anything.
  You cannot see any unusual indistinct traces of anything.
  ```

  Do not generalize these examples into an unrestricted regex before further
  evidence.
- **LOG-CONFIRMED:** a queue announcement can appear during Analyse:
  `After you finish analysing you will start mining.` Another queue line,
  `After you finish mining you will start analysing.`, may appear before a real
  start. **FROZEN DECISION:** a queue announcement never starts Analyse. An
  unrelated/noise line during active Analyse is not automatically an
  `AnalyseTrace` and cannot by itself end a valid sequence unless it is a hard
  boundary. This rules out treating every line between boundaries as a trace.
- **FROZEN DECISION:** Analyse later distinguishes at least analysed input
  kinds Shard and Ore. This does not require a public model yet; the boundary
  parser must first recognize both variants.
- **FROZEN DECISION:** Analyse has its own observational `AnalyseSequence`, is
  not a `ProspectingResult` or NearbyScan, and never creates
  `ActiveVeinContext`, synchronizes/decrements Remaining, validates a vein, or
  changes Mining counters. Trace observations remain diagnostic/observational.
- **FROZEN DECISION:** do not require all six stages for an Analyse sequence;
  preserve trace order and repeated occurrences, including the same material
  within one stage. Direction is optional: supported traces without it are
  retained without a map position. The domain retains observed strength and
  quality labels; the UI displays the reference distance as an approximate ring,
  not an exact deposit location. QL ranges remain reference knowledge and do
  not become calculated runtime results. Unknown material and explicit no-trace
  observations remain distinct.
- **FROZEN DECISION — Analyse UI:** keep MainWindow minimal. After the
  first valid completed Analyse result is published for a profile,
  show a small button with an icon and the label `Analyse` on that profile's
  Mining Skill row. A start or incomplete
  sequence alone does not show the link; a completed no-trace result qualifies.
  Clicking the link opens a separate non-modal results window showing Shard/Ore,
  recognized traces, materials, quality, directions, and no-trace observations.
  Retain the last completed result separately per profile. A new result updates
  an existing window; it neither opens the window automatically nor creates a
  duplicate. Use existing UI and notification mechanisms without additional I/O.
  This presentation does not alter Mining counters, Prospecting, Vein Context,
  or Remaining. Clicking another profile's button selects it in the existing
  window.
- **OPEN / DATA-NEEDED:** still unresolved are the complete analysed-item type
  list, complete item-description list, complete trace-material list, local
  complete compass conditions, all no-trace wordings,
  interruption/cancel/abort grammar, exact finalization on stream gap/reset/
  hard boundary beyond the frozen clear-state rule, and grade mapping to
  `QualityMeasurement.Grade`.
### Analyse implementation status

- **IMPLEMENTED:** `AnalyseLineParser` recognizes shard and ore start/finish,
  confirmed stage markers, trace and no-trace forms, with timestamp
  normalization. Observations retain the original line; traces also retain
  strength, material, optional grade and direction. Confirmed directionless traces
  retain a null direction. The parser is stateless and does
  not assemble a sequence.
- **IMPLEMENTED:** a per-profile `AnalyseSequenceAssembler` and separate
  Context consumer publish an immutable last result after a matching finish
  with at least one trace or no-trace observation. The matching finish ends the
  observed sequence; it does not prove that all possible messages were recognized.
  A valid timestamp on that finish is retained as the result's time of day;
  otherwise the time is unavailable.
  Entries retain order and
  duplicates, including stage markers. A new start abandons the active
  sequence; a mismatched finish does not publish it. Reset, continuity loss,
  invalidation and session boundaries clear state; a drained profile release
  abandons the partial sequence while retaining the last completed snapshot.
  Reset Session also clears snapshots retained after a profile release.
- **IMPLEMENTED:** a separate non-modal Analyse results window reads the
  selected profile's latest snapshot and follows processor notifications on
  the WPF Dispatcher. It supports changing profile and unsubscribes on close.
  MainWindow exposes a compact per-profile button only for completed results,
  including profiles with no other displayed data; it reuses one window.
- **FROZEN DECISION:** Analyse is not required for Mining counting,
  `ActiveVeinContext`, Prospecting Remaining tracking, `MiningAttempt`, or
  guarded Remaining decrement. It must therefore not block those features.
- **FROZEN DECISION:** Analyse assembly collects recognized observations and
  markers, not arbitrary lines.

### Complete, partial, and special-result finalization

- **FROZEN DECISION:** standard exact vein result is complete only after
  Material + Quality + Remaining. A same-material material-only (`TypeOnly`)
  observation may preserve a stronger existing `ActiveVeinContext`; a
  material-only observation for a different material may replace it as
  `TypeOnly`. A `Partial` Material + Quality observation may create or replace
  the current context as `Partial`, while a complete synchronized result
  replaces it as the authoritative observation. A contradictory material also
  follows the existing replacement lifecycle and becomes `TypeOnly`.
  Incomplete state may be abandoned by new start/stop or cleared by reset, gap,
  or hard boundary.
- **IMPLEMENTED — RockSalt direct-Salt finalization:**
  `RockSalt → Quality (ExactMax or Grade) → direct Salt` finalizes a
  `ProspectingResult.Vein` with `Remaining.Unknown` and empty `Augments`.
  The Quality precision is preserved; Salt is a direct RockSalt Prospecting
  finalizer, not a Remaining measurement, and is not assigned to `Augments`.
  `RockSalt → Grade → Salt` is confirmed by 72 ProfileA real-log sequences, all
  observed as `Grade(VeryGood)`; generic Grade support follows the shared
  `QualityMeasurement.Grade` contract.
- **LOG-CONFIRMED:** audit 76A confirms 83 direct RockSalt observations, all
  with `Material → Quality → Salt`: 72 `Grade(VeryGood)`, ten `ExactMax`,
  and the one historical alias with `Grade(Poor)`. It found zero confirmed
  MaterialOnly, Quality-without-Salt, or Salt-without-Quality observations.
  This evidence confirms no runtime gap and establishes no skill rule or
  threshold.
- **LOG-CONFIRMED / IMPLEMENTED — RockSalt historical alias:** exact
  `You would mine salt rock salt here.` is a direct-material alias for the
  existing `VeinMaterial.RockSalt`, alongside `You would mine rock salt here.`
  It occurred once (ProfileA, 2016-12-09); the observed `Grade(Poor) → Salt` tail
  uses the existing direct-Salt lifecycle. This is not a new material or
  pipeline, and does not imply fuzzy, substring, or general `salt` matching.
- **OPEN / DATA-NEEDED — low-precision or non-Salt RockSalt:** audit 76A
  found no confirmed MaterialOnly, Quality-without-Salt, or
  Salt-without-Quality direct RockSalt sequence. Finalization policy for those
  shapes, including `RockSalt → Quality` without direct Salt or Flint,
  remains unresolved; do not add a rule or skill threshold without real data.
- **FROZEN DECISION:** RockSalt material-only plus Salt does not finalize; a
  normal `VeinMaterial → Grade` likewise continues to await Remaining/band.
  The direct Salt finalizer is not general-purpose for other materials.
- **DOMAIN NOTE:** player observation indicates that a RockSalt vein has a
  small real-world resource, around several dozen extractions (approximately
  50), but no exact limit is confirmed. The executable contract therefore
  remains `Remaining.Unknown`; it assigns neither Exact Remaining nor an
  estimate/ordinary ore depletion band.
- **LOG-CONFIRMED:** the observed RockSalt-without-Remaining sequence contains
  ExactMax Quality followed directly by Salt, then queue messages, an actual
  Wall Mining start, and `You mine some rock salt.`. Queue messages are not
  finalizers; neither Mining start nor general Salt/Flint association is needed
  for the frozen direct-Salt finalization rule.
- **LOG-CONFIRMED:** PlainRock may be MaterialOnly or may be followed by
  `ExactMax` or any supported `Grade` Quality; it never has Remaining. Audit
  71A confirmed 74 unique MaterialOnly actions ending at an unambiguous next
  action boundary. MaterialOnly publishes immediately, while later Quality is
  an optional precision upgrade. Do not add a timeout, fixed lookahead, or a
  skill-based branch.
- **IMPLEMENTED:** the supported association policy is deliberately narrow:
  contiguous exact tails only, immutable replacement, first-seen order, and
  duplicate no-op. It does not use timeout or timestamp correlation and does
  not alter Mining counters, Remaining, MiningAttempt, or current UI
  presentation. Direct Flint and RockSalt Flint remain **DATA-NEEDED**.

### Materials and direct-result evidence

- **LOG-CONFIRMED:** exact direct Prospecting literals are:

  ```text
  You would mine iron ore here.
  You would mine copper ore here.
  You would mine tin ore here.
  You would mine zinc ore here.
  You would mine lead ore here.
  You would mine silver ore here.
  You would mine gold ore here.
  You would mine sandstone shards here.
  You would mine marble shards here.
  You would mine slate shards here.
  You would mine rock salt here.
  ```

- **LOG-CONFIRMED / IMPLEMENTED:** `You would mine gold ore here.` occurs 75
  times in ProfileA real Event logs. It follows the existing direct-vein pipeline:
  Material → Quality → Remaining/EstimateBand → synchronized `ActiveVeinContext`
  with `Material = Gold`; it has no Gold-specific semantics.
- **FROZEN DECISION:** `VeinMaterial` maps to yield kind as follows:

  | Materials | YieldKind |
  | --- | --- |
  | Iron, Copper, Tin, Zinc, Lead, Silver, Gold | Ore |
  | Sandstone, Marble, Slate | Shards |
  | RockSalt | RockSalt |

  Gold → Ore uses the existing generic Ore wall-binding and Exact Remaining
  decrement semantics for a compatible local `Ore` primary; it has no separate
  Gold-specific mechanism.

## Measurements and confidence

### Remaining

- **FROZEN DECISION:** `RemainingMeasurement` supports independently known
  Exact values and estimate bands.
- **FROZEN DECISION:** `Exact(0)` is valid.
- **FROZEN DECISION — post-zero policy:** `Exact(0)` remains in
  `ActiveVeinContext` as the last snapshot and its Confidence remains
  `Tracking`. It does not qualify the context for a new Wall binding: a later
  Wall start creates an unbound `MiningAttempt`, and no further decrement is
  allowed until a new complete Prospecting synchronizes the context. This does
  not decide UI, an Exhausted state, or physical vein disappearance.
- **FROZEN DECISION:** frozen estimate bands are:

  | Band | Range |
  | --- | --- |
  | Plenty | 10001+ |
  | FewWeeks | 5000–10000 |
  | StartingToDeplete | 3000–5000 |
  | ProspectAnother | 1000–3000 |
  | RunOutSoon | 100–1000 |
  | RunOutAnyHour | 1–100 |

- **FROZEN DECISION:** common edge values are conservatively accepted by both
  adjacent bands until empirical evidence establishes inclusivity.
- **EMPIRICAL PROVENANCE — Remaining (77B):** the currently replayable corpus
  contains 129 `_Event` files from 2015–2018. It confirms the five estimate
  literals `FewWeeks`, `StartingToDeplete`, `ProspectAnotherVein`,
  `RunOutSoon`, and `RunOutAnyHour`. It contains zero Exact Remaining literals.
  Exact Remaining nevertheless remains a **HISTORICALLY CONFIRMED** real flow
  from the broader 62A corpus; its raw logs are unavailable, so it cannot be
  recounted or replayed. `Plenty` has zero occurrences in the currently
  available corpus and remains **DATA-NEEDED**. This provenance changes no
  runtime rule.
- **FROZEN DECISION — qualitative Remaining display policy:** an exact
  descriptive Remaining literal from the Event log is only the recognition
  trigger for its `EstimateBand`; it is not UI text. The UI displays the
  corresponding approximate numeric range instead. For example,
  `Only a few weeks mining remain here.` recognizes `EstimateBand.FewWeeks`
  and displays `≈ 5,000–10,000`.
- **FROZEN DECISION:** `EstimateBand` is not `Exact` Remaining and must never
  be locally decremented after Mining. A later direct Prospecting observation
  replaces the estimate with its newly observed Remaining measurement.

### Quality

- **FROZEN DECISION:** Quality is also a continuity validator, not merely a
  display value.
- **FROZEN DECISION:** the measurement model includes `Grade` and `ExactMax`.
- **FROZEN DECISION — Quality precision contract:** the domain expectation is
  `Grade` for skill ranges 20–59 and 60–90, and numeric `ExactMax` above 90.
  This describes which quality literal the game is expected to emit; the
  parser does not branch on skill and continues to interpret the actual
  Event-log literal.
- **VALIDATION-NEEDED — empirical threshold validation:** audit 72A paired
  real `_Event` and `_Skills` data only where a Prospecting update was within
  30 minutes: MaterialOnly occurred at `1.123–19.139`, while Grade occurred at
  `33.339–61.073`. **DATA-NEEDED — evidence gap:** there is no reliable data
  for `20–33.339`, `80+`, `90+`, or `91+`, and no reliable paired ExactMax
  result. These are correlations, not
  confirmed thresholds; they do not alter the selected contract or introduce
  skill-based runtime logic.

### Confidence

- **FROZEN DECISION:** `Unknown` means no reliable active context.
- **FROZEN DECISION:** `TypeOnly` means material is known, but Quality and
  Remaining are not synchronized.
- **FROZEN DECISION:** `Partial` means material and Quality are known
  from the current Prospecting observation while Remaining remains Unknown.
- **FROZEN DECISION:** `Synchronized` follows a complete direct Prospecting
  result.
- **FROZEN DECISION:** `Tracking` follows an eligible local Mining decrement
  from a synchronized exact context.
- **FROZEN DECISION:** `Ambiguous` means continuity of the same physical vein
  cannot be safely asserted.

## Examine

- **LOG-CONFIRMED:** Examine can provide a vein material without a complete
  Prospecting result.
- **LOG-CONFIRMED:** exact Examine literal
  `A vein of crystalized sediment formed over time by evaporation.` maps to
  `VeinMaterial.RockSalt`. It occurs repeatedly during confirmed continuous
  mining of a RockSalt vein.
- **IMPLEMENTED:** the exact RockSalt Examine mapping is implemented with timestamp
  normalization and covered by parser and processor tests. Without a context it
  creates `RockSalt/TypeOnly`; a matching RockSalt context is not mutated; and
  a conflicting context is replaced by `RockSalt/TypeOnly` with Unknown
  Quality/Remaining. A pre-existing Wall `MiningAttempt` retains its immutable
  binding and is not retargeted.
- **FROZEN DECISION:** Examine never decrements Remaining and never changes
  Mining counters.
- **FROZEN DECISION:** an Examine matching the active material preserves its
  Quality and Remaining.
- **FROZEN DECISION:** a conflicting Examine cannot retain prior
  Quality/Remaining under the new material.
- **FROZEN DECISION:** a conflicting Examine replaces the context with
  `TypeOnly`, the newly examined material, and Unknown Quality/Remaining.
- **OPEN / DATA-NEEDED:** no Event-log identity proves that two same-material
  Examine messages refer to the same physical vein.

## Mining action context and depletion

- **FROZEN DECISION:** Wall Mining actions `mine forwards`, `mine upwards`,
  and `mine downwards` are depletion-eligible.
- **FROZEN DECISION:** Floor and Ceiling Mining are off-target relative to an
  active wall vein. Their primary yield is counted, but it does not decrement
  that wall vein's Remaining.
- **FROZEN DECISION:** bare `You start to mine.` is Surface Mining and a hard
  boundary.
- **FROZEN DECISION:** `You start to tunnel.` is Tunnelling and a hard
  boundary.
- **FROZEN DECISION:** queue messages beginning `After you finish ...` never
  start a Mining action.

### Primary results and bonuses

- **FROZEN DECISION:** distinguish two concepts. Primary Mining results for
  counters are StoneShards, Ore, Shards, or RockSalt; each increments its own
  counter and TotalCount. The only vein-depletion-compatible primary results
  for an active wall vein are Ore, Shards, and RockSalt.
- **FROZEN DECISION:** `You mine some stone shards.` maps to
  `MiningMaterial.StoneShards`: it is primary for counters, but is not a
  depletion-compatible yield for an active wall vein.
- **FROZEN DECISION:** Salt, Flint, gems, and crystals are bonuses/byproducts
  and never decrement Remaining.
- **FROZEN DECISION:** the complete primary set is StoneShards, Ore, Shards,
  and RockSalt. The complete byproduct set is Salt, Flint, SourceCrystal,
  Sapphire, Diamond, Emerald, Ruby, and Opal. Both confirmed SourceCrystal
  log aliases share that one canonical counter. A primary adds
  one material count and one `TotalCount`; a byproduct adds only its own
  material count and adds neither `TotalCount` nor Remaining decrement.
- **FROZEN DECISION:** exact local `You get a source crystal.` is
  `MiningMaterial.SourceCrystal`, a byproduct under the current project policy. No inference about its
  actual game-action source is made by this local literal.
- **FROZEN DECISION:** StoneShards produced while mining Floor or Ceiling do
  not decrement an active wall-vein context.
- **FROZEN DECISION:** an Exact Remaining decrement requires all of: local
  successful primary result, active exact context, compatible action context,
  compatible `YieldKind`, and no bonus/byproduct-only result.
- **FROZEN DECISION:** the decrement changes `Synchronized` to `Tracking`.

### Mining Inspiration correlation

- **FROZEN DECISION:** the exact local message `You have a moment of
  inspiration...` alone does not increase `TotalCount`, any `MaterialCount`,
  or Moment of Inspiration; it does not decrement Remaining and does not
  represent a confirmed Rare, Supreme, or Fantastic result.
- **FROZEN DECISION:** only the strict per-profile sequence
  `recognized local Mining/Tunnelling start → MOI → qualifying local primary`
  confirms one Moment of Inspiration. The normal primary yield then performs its existing one
  material-counter increment and one `TotalCount` increment, and may perform
  at most one already-eligible Remaining decrement. MOI is never inferred from
  gems, byproducts, third-person Mining, or MOI outside an active local mining
  generation.
- **FROZEN DECISION:** MOI is neither an additional Mining action nor an
  additional extraction. It must never create a second primary count or a
  second Remaining decrement.
- **LOG-CONFIRMED / IMPLEMENTED:** `The wall will break soon.` is same-action
  informational warning. A pending MOI survives it and the following qualifying
  local primary confirms one Moment of Inspiration. The real-log logical replay
  after this fix is ProfileA = 5, ProfileB = 2, ProfileC = 0; these are not
  persisted session/UI counters. `The wall breaks!` remains outside this MOI
  change because no real MOI case justified extending it.
- **NOT OBSERVED:** `A deep shaft will probably emerge.` shares the technical
  `InformationalWarning` transition, but no current real log line exactly
  matching that literal was observed; it is not confirmed real-data MOI
  behavior.
- **DATA-NEEDED:** Rare, Supreme, and Fantastic tiers remain unresolved. MOI
  alone must not be interpreted as confirmation of any tier.

### Warnings, failures, and wall break

- **FROZEN DECISION:** distinguish pre-start rejection, informational warning,
  actual no-yield terminal, and wall-break phase. They are not interchangeable.
- **FROZEN DECISION:** the following categories have no safe decrement:
  `You're too busy.`, too-far, missing-tool, no-space, cannot-reach-ceiling,
  reinforcement blockers, and geometry/highway/open-space restrictions.
- **FROZEN DECISION:** `You stop mining.` and confirmed no-yield terminal
  messages end the attempt without depletion.
- **LOG-CONFIRMED:** in active Wall Mining, `You see dark dungeons.` can occur
  between `You start to mine forwards.` and a qualifying primary. It is
  same-action information for Vein Context/`MiningAttempt`: it does not reset
  `ActiveVeinContext`, close the attempt, start a new action, or represent an
  action result. **NOT OBSERVED for MOI:** none of the 17 current real explicit
  MOI instances placed this line between MOI and its outcome, so no MOI
  transparency rule or tracker change is asserted.
- **LOG-CONFIRMED:** `The wall breaks!` alone does not decrement Remaining.
- **FROZEN DECISION:** wall break is a phase of the active Wall
  `MiningAttempt`; the following compatible final primary result may perform
  the one final decrement, may reach approximately zero, and then closes or
  destroys the context. It must never double-decrement.

## Continuity boundaries

Hard boundaries are:

- **FROZEN DECISION:** application start.
- **FROZEN DECISION:** reconnect and `Logging started`.
- **FROZEN DECISION:** Reset Session.
- **FROZEN DECISION:** `StreamContinuityLost`.
- **FROZEN DECISION:** Nearby Scan / `There is only rock nearby.`.
- **FROZEN DECISION:** Surface Mining start.
- **FROZEN DECISION:** Tunnelling start.
- **FROZEN DECISION:** hard material, Quality, or Remaining contradiction.
- **FROZEN DECISION:** incompatible otherwise-eligible Wall primary result.
- **FROZEN DECISION:** destruction.

On a hard boundary, partial Prospecting, MiningAttempt, BreakPending, and
unsafe vein continuity are cleared or invalidated according to the selected
confidence policy. The exact `Unknown` versus `Ambiguous` policy after
reconnect/`Logging started` remains **OPEN / DATA-NEEDED**.

## Canonical rules and implemented behavior

The sections below extend the core evidence registry with current frozen rules
and implementation-specific domain behavior. Historical divergence tables and
implementation checkpoints were moved to the archive and are not current
authority.

### Quality, EstimateBand, and contradictions

- **FROZEN DECISION — Prospecting threshold and range policy:** implementation
  does not require a separate LOG-CONFIRMED Event-log example for every
  Prospecting quality threshold or remaining range. Community/player-confirmed
  Quality and Remaining thresholds and ranges may be implemented. Future real
  logs verify or correct those decisions; they do not block implementation.
  The parser must still react only to actual Event-log message literals, and
  player skill is not an input required for parsing.
- **FROZEN DECISION — Prospecting result levels:**

  | Level | Quality | Remaining |
  | --- | --- | --- |
  | Material only | `QualityMeasurement.Unknown` | `RemainingMeasurement.Unknown` |
  | Estimated | `QualityMeasurement.Grade` plus its display range | `RemainingMeasurement.EstimateBand` plus its display range |
  | Exact | `QualityMeasurement.ExactMax` | `RemainingMeasurement.Exact` |

### Implemented qualitative Prospecting

- **IMPLEMENTED — unified progressive Prospecting:** player skill is not an input to
  parsing or pipeline selection. One per-profile observation path publishes
  every recognized increment: Material produces `TypeOnly`; Material plus
  Quality (`Grade` or `ExactMax`) produces `Partial`; Material plus Quality and
  Remaining (`EstimateBand` or Exact) produces `Synchronized`. A guarded local
  Exact depletion alone moves an eligible context to `Tracking`. Each new
  Prospecting start begins a new observation, so data cannot mix across veins.

- **LOG-CONFIRMED / IMPLEMENTED — MaterialOnly Vein:** `Prospecting start →
  Material → action boundary` is a complete lower-precision observation when
  Wurm supplies no Quality or Remaining. Audit 73A confirmed 13 real normal
  Vein sequences of this form. Recognized Material immediately creates
  `ActiveVeinContext` with `QualityMeasurement.Unknown`,
  `RemainingMeasurement.Unknown`, and `VeinConfidence.TypeOnly`. UI presents
  the Material and confidence while hiding Unknown Quality and Remaining.
  TypeOnly is not eligible for Wall binding or local Exact Remaining decrement.
  A later Quality or Remaining line is an optional precision upgrade; stopping
  Prospecting creates no additional context. This is distinct from an assembler
  that has not yet received Material.
- **IMPLEMENTED — qualitative Quality:** `QualityMeasurement.Grade` supports `Poor`,
  `Acceptable`, `Normal`, `Good`, `VeryGood`, and `Utmost`. The exact,
  case-sensitive descriptive Event-log literals are timestamp-normalized. UI
  displays `Poor quality (20–29)`, `Acceptable quality (30–39)`, `Normal quality
  (40–59)`, `Good quality (60–79)`, `Very good quality (80–94)`, and
  `Utmost quality (95–99)`. `ExactMax` and `Unknown` retain their distinct UI
  representations.
- **LOG-CONFIRMED / IMPLEMENTED — PlainRock Quality finalization:** an existing
  real PlainRock material followed by valid Quality (`ExactMax` or any supported
  `Grade`) upgrades a valid MaterialOnly `ProspectingResult.PlainRock` to a
  more precise result. Every PlainRock information level clears a prior
  `ActiveVeinContext` and creates no replacement Vein Context.
- **IMPLEMENTED — Estimated Remaining:** `RemainingMeasurement.EstimateBand` supports
  `Plenty`, `FewWeeks`, `StartingToDeplete`, `ProspectAnotherVein`,
  `RunOutSoon`, and `RunOutAnyHour`. UI displays respectively `≈ 10,000+`,
  `≈ 5,000–10,000`, `≈ 3,000–5,000`, `≈ 1,000–3,000`, `≈ 100–1,000`, and
  `≈ 1–100`. An EstimateBand is never locally decremented.
- **IMPLEMENTED — complete qualitative Prospecting:** `Material → Grade → EstimateBand`
  finalizes a normal `ProspectingResult.Vein` and upgrades the TypeOnly context
  to a `Synchronized ActiveVeinContext`. Re-prospecting may replace either an
  estimated or an exact context with its new observation.
- **IMPLEMENTED — Exact Remaining delta indicator:** only an explicitly signalled
  `LocalExactDecrement` may produce a transient UI indicator. The display is
  `↓N <Remaining>`, with the delta before the current Exact value. Fast local
  decrements accumulate from the value before the series and restart an
  approximately one-second timeout. The indicator is absent for re-prospecting,
  resynchronization, replacement, clear, EstimateBand, and Unknown.
- **IMPLEMENTED:** automated tests and live testing validate Material-only,
  Estimated, Exact plus delta-indicator flows, and the independent Mining
  Counter path.

### Vein emergence

- **LOG-CONFIRMED / IMPLEMENTED:** the timestamp-normalized grammar
  `A vein of pure <material> emerges here.` is recognized only when
  `<material>` maps to an existing `VeinMaterial`; unknown materials are not
  inferred.
- **LOG-CONFIRMED / IMPLEMENTED:** `A vein of pure gold emerges here.` occurs
  16 times in real Event logs. Its existing pure-vein path resolves `gold` to
  `VeinMaterial.Gold` and creates only a Gold `TypeOnly` context with Unknown
  Quality and Unknown Remaining.
- **FROZEN DECISION:** emergence is a new current-vein observation
  and uses the same `ProspectingObservationUpdate -> ActiveVeinContext` path as
  progressive Prospecting. It creates Material plus Unknown Quality, Unknown
  Remaining, and `TypeOnly`; it replaces prior context data rather than
  retaining old Quality or Remaining.
- **FROZEN DECISION / IMPLEMENTED:** emergence neither binds local depletion
  nor changes Mining counters or Total. An already bound MiningAttempt retains
  its immutable prior target and is not retargeted.
- **IMPLEMENTED — emergence inside fresh Prospecting:** after a Prospecting
  start, a recognized emergence preserves the assembler only while it still
  awaits its first direct Material or PlainRock. Emergence still publishes its
  existing TypeOnly observation; a later direct result then follows the
  existing progressive lifecycle. This does not preserve every active sequence:
  emergence still resets an assembler that already captured Material, PlainRock,
  Quality, or the RockSalt direct-Salt waiting state.
- **LOG-CONFIRMED / IMPLEMENTED:** audit 62A identified 25 complete
  `start → emergence → later direct result` sequences (ProfileA: 3 PlainRock +
  ExactMax, 3 Iron, 1 Copper; ProfileB: 18 Copper + Grade + FewWeeks), plus
  one PlainRock + Grade B1 overlap. The archived logs are not in this repository;
  coverage is established by that audit, verified state transitions, and
  regression tests rather than a post-fix replay.
- **IMPLEMENTED:** state is isolated per `ProfileName`; automated tests and live
  multi-profile validation confirm independent contexts.

- **FROZEN DECISION — Remaining decrement UI policy:** after a confirmed local
  depletion, `RemainingMeasurement.Exact` decreases by one and the UI displays
  the new value. For a short period it also displays `↓1` beside that value;
  the Remaining value may be temporarily highlighted. This effect belongs
  exclusively to a local decrement. Re-prospecting or other resynchronization
  shows the newly observed value without `↓1`.
- **FROZEN DECISION:** an estimated Remaining UI displays only its approximate
  range, for example `≈ 5,000–10,000`; it receives neither a local decrement
  nor a `↓1` effect. `RemainingMeasurement.Unknown` displays `Unknown` and
  receives no effect.
- **FROZEN DECISION — estimated Quality UI policy:** the descriptive Event-log
  quality message is the recognition trigger. The UI displays the short grade
  name and its range, for example `Good quality (60–79)`, rather than the full
  Event-log message.

- **FROZEN DECISION:** Exact Remaining and named estimated Remaining are
  separate forms. The frozen bands are Plenty 10001+, FewWeeks 5000–10000,
  StartingToDeplete 3000–5000, ProspectAnother 1000–3000, RunOutSoon 100–1000,
  and RunOutAnyHour 1–100.
- **FROZEN DECISION:** shared boundary values are temporarily valid in both
  adjacent bands until empirical data proves otherwise. Exact(0) is valid. A
  tracker may never decrement below zero or perform another depletion of that
  exact context without new synchronization. The post-zero binding policy is
  defined above; UI and diagnostic representation remain open.
- **OPEN / DATA-NEEDED:** only exact parser wording for estimated bands is
  missing; the existence of the model and its bands is not data-blocked.
- **FROZEN DECISION:** Quality is immutable continuity validation data.
  `Grade` and `ExactMax` both belong to the model; matching exact Quality can
  support continuity while a hard mismatch is a continuity contradiction.
- **FROZEN DECISION:** the selected Quality precision contract remains Grade
  through 90 and numeric ExactMax above 90. Future real logs may provide
  independent empirical validation against skill. Audit 72A confirms only the
  MaterialOnly/Grade ranges recorded above and no paired ExactMax; exact
  thresholds remain **VALIDATION-NEEDED**. The parser still uses the observed
  literal rather than skill-based branching.

### Confirmed Mining action literals

- **LOG-CONFIRMED / IMPLEMENTED:** real Event logs contain `You start to mine
  up.` 17,734 times and `You start to mine down.` 4,404 times. They are exact
  aliases of the existing `You start to mine upwards.` / `You start to mine
  downwards.` Wall starts and map to `MiningActionContext.Wall`; no other
  Mining-start aliases were added from this evidence.
- **LOG-CONFIRMED:** Wall starts also include `You start to mine forwards.`,
  `You start to mine upwards.`, and `You start to mine downwards.`.
- **FROZEN DECISION:** every one is depletion-eligible Wall Mining and starts
  a Wall `MiningAttempt`; eligible Exact Remaining uses the existing guarded
  single decrement, and the start establishes the Mining action generation for
  Moment of Inspiration correlation.
- **LOG-CONFIRMED:** Floor and Ceiling starts are `You start to mine the
  floor.` and `You start to mine the ceiling.`.
- **FROZEN DECISION:** Floor/Ceiling are off-target for an active wall vein.
  Their successful primary result is counted, but does not decrement wall
  Remaining, does not retarget the wall context, and is not by itself a hard
  contradiction. Example: Iron wall context → floor/ceiling → `You mine some
  stone shards.` increments Mining counters while wall Remaining is unchanged.
- **FROZEN DECISION / IMPLEMENTED:** Ceiling is not a Vein Context boundary.
  It preserves the existing wall `ActiveVeinContext`, creates an unbound Ceiling
  `MiningAttempt`, and clears only the matching stale surface Nearby snapshot.
- **LOG-CONFIRMED:** `You start to mine.` is Surface Mining.
- **FROZEN DECISION:** Surface Mining is a hard boundary for underground vein
  continuity and must not be confused with Wall Mining.
- **LOG-CONFIRMED:** `You chip away at the rock.` and `You realize you can't
  open a tunnel here, because the surface is too flat.` occur in confirmed
  Surface-related sequences.
- **LOG-CONFIRMED:** Tunnelling literals include `You start to tunnel.` and
  `You create a tunnel entrance!`.
- **FROZEN DECISION:** Tunnelling is an explicit action context and hard
  boundary, never Wall Mining. It is represented by the current
  `MiningActionContext` model and parser.
- **FROZEN DECISION:** any `After you finish ...` line is only a queue
  announcement: it never starts MiningAttempt/Prospecting, changes action
  context, or decrements Remaining.

### TotalCount, local player, and re-prospecting

- **FROZEN DECISION:** primary Mining results StoneShards, Ore, Shards, and
  RockSalt increment their material counter and `TotalCount`.
- **FROZEN DECISION:** Salt, Flint, SourceCrystal, Sapphire,
  Diamond, Emerald, Ruby, and Opal may have their own counters, but do not
  increment `TotalCount` and never decrement Remaining. RockSalt + Salt is
  therefore RockSalt +1, Salt +1, TotalCount +1.
- **FROZEN DECISION:** only local `You ...` successful primary results affect
  local counters and local depletion tracking. Third-person activity is
  diagnostic-only and does not directly decrement tracked Remaining.
- **FROZEN DECISION:** a new complete exact Prospecting is the new world
  source-of-truth: it replaces tracked Remaining, changes Tracking to
  Synchronized, clears any transient local-decrement indicator, and never resets
  MiningSessionCounters.
- **FROZEN DECISION:** `ExternalDepletion` is diagnostics only. Example:
  Prospecting 1000 → local 5 → expected tracked 995 → next exact 987 exposes 8 of
  external/world difference. It never mutates Mining history or attributes a
  player.

### Authoritative re-prospecting versus hard contradiction

- **LOG-CONFIRMED / IMPLEMENTED:** timestamp-normalized descriptive material
  identification includes Marble: `A vein of an interlocking mosaic of
  carbonate crystals, otherwise known as marble, emerges here.` and Slate:
  `A vein of fine-grained, foliated, homogeneous metamorphic rock, or slate as
  we know it, emerges here.` Both create the same observation path as pure
  emergence. Sandstone and RockSalt remain exact `VeinExamineParser` literals.
- **FROZEN DECISION:** Prospecting, Vein Emergence, and Vein Examine
  use one `ApplyMaterialObservation` lifecycle. A same-material observation is
  benign confirmation and does not degrade Exact/Synchronized/Tracking. A
  different material replaces the active context as Material-only/TypeOnly with
  Unknown Quality and Remaining; prior Quality/Remaining are not retained.
- **FROZEN DECISION:** before applying a different material, an active
  attempt whose `BoundVeinContext.Material` differs is cleared. The former
  attempt cannot subsequently decrement the old Remaining, retarget to the new
  material, or clear the replacement context in `WallBrokenAwaitingPrimary`.

- **FROZEN DECISION:** every new complete direct Prospecting result is a new,
  authoritative world observation. It may change Material, Quality, and
  Remaining, replace the prior context, and move Tracking to Synchronized.
  A difference from the previous context is not by itself a hard
  contradiction: it may reflect natural depletion, ExternalDepletion, a new
  synchronization, or a different vein.
- **FROZEN DECISION:** Quality is an immutable validator only during continuity
  that the system still treats as the same physical vein without a new complete
  synchronization point. A different Quality from a new complete Prospecting
  result is not rejected; it synchronizes a new context.
- **FROZEN DECISION:** a hard contradiction applies only to data that cannot be
  reconciled within continuing continuity/an active attempt, such as an
  incompatible eligible Wall primary or impossible Material/Quality/Remaining
  state. It does not apply to a new complete Prospecting result by itself.

### MiningAttempt, wall break, and failures

- **FROZEN DECISION:** at most one `MiningAttempt` exists per profile. Its
  conceptual fields are action context, target compatibility, phase, terminal
  result expectation, and wall-break phase. `BreakPending` may be that phase,
  rather than separate global state.
- **FROZEN DECISION:** MiningAttempt binds target at confirmed actual action
  start, never from later Examine/observation. Wall forwards/upwards/downwards
  may become a depletion candidate for the then-active wall-compatible context
  and retain that binding throughout lifecycle. Later Examine cannot retarget
  the attempt.
- **FROZEN DECISION — qualified Wall binding predicate:** a Wall
  `MiningAttempt` receives `BoundVeinContext` only when, at the confirmed Wall
  action start, `ActiveVeinContext` is non-null, its Confidence is
  `Synchronized` or `Tracking`, its Quality is `QualityMeasurement.ExactMax`,
  and its Remaining is `RemainingMeasurement.Exact { Value: > 0 }`. `TypeOnly`, `Ambiguous`,
  and `Unknown` confidence; absent `ExactMax` Quality; or absent Exact
  Remaining do not qualify. Without a qualifying context, Mining remains
  normal for counters, the Wall attempt is admitted with
  `BoundVeinContext = null`, and it must never be retargeted later.
- **FROZEN DECISION:** Floor/Ceiling create off-target attempts relative to a
  wall context: primary counts, but cannot decrement or contradict that wall
  context. Surface/Tunnelling are hard boundaries and do not create depletion
  attempt for the old wall context. If Wall starts with insufficient context,
  Mining still counts normally but no vein decrement is allowed.
- **FROZEN DECISION:** Floor and Ceiling each create an Active
  `MiningAttempt` with `BoundVeinContext = null`; the existing
  `ActiveVeinContext` remains unchanged. Under the per-profile lock, Surface
  and Tunnelling clear partial `ProspectingSequence`, `ActiveVeinContext`, and
  any Vein `MiningAttempt`; they do not create a new Vein attempt. The same
  full invalidation applies to Nearby Scan / `There is only rock nearby.`.
  Mining counters, Total, and Moment of Inspiration remain independent.
- **FROZEN DECISION:** `ResetProfileBarrier`,
  `StreamContinuityLost`, and `Invalidate(profileName)` clear
  `ActiveMiningAttempt` together with other continuity-dependent state.
  Prospecting or Examine after attempt admission may update the current
  `ActiveVeinContext` under their own rules, but never change the existing
  attempt's `BoundVeinContext`.
- **FROZEN DECISION:** action start, warning, and wall break never decrement;
  only a final compatible successful primary result may decrement exactly once.
  A failed/no-yield terminal closes the attempt without depletion.
- **LOG-CONFIRMED:** `The wall will break soon.` is a warning and `The wall
  breaks!` is followed in real sequences by final `You mine ...`.
- **FROZEN DECISION:** wall-warning is informational; wall-break changes phase;
  the final compatible primary may perform one last decrement, may reach about
  zero, and then ends MiningAttempt. `The wall breaks!` does not remove context
  immediately; it waits for final compatible primary. If destruction is
  confirmed, the old target cannot remain a normal Synchronized/Tracking target
  nor be decremented again. No double decrement.
- **OPEN / DATA-NEEDED — POST-DESTRUCTION REPRESENTATION:** choose later
  between removing context and retaining an Ambiguous/destroyed diagnostic
  snapshot. This does not change the operational safety rule above.
- **NOT OBSERVED for the current exact literal:** `A deep shaft will probably
  emerge.` shares the technical informational-warning classification, but the
  current real logs do not contain that exact line. It is not documented as
  confirmed real-data MOI behavior.

| Failure class | Examples | Required behavior |
| --- | --- | --- |
| Pre-start rejection | `You're too busy.`, too far, missing tool, no space, cannot reach ceiling, reinforcement and geometry/highway/open-space blockers | No decrement and no false attempt admission. |
| Informational warning | `The wall will break soon.`; technical deep-shaft literal not observed in current real logs | Attempt remains active; no decrement. |
| Actual no-yield terminal | `You stop mining.`, context-confirmed Surface/Tunnel `You chip away at the rock.`, `The cave walls echo strangely...`, unstable wall ... `so you stop mining`, context-sensitive surface-too-flat | Close attempt; no decrement. |
| Wall-break phase | `The wall breaks!` | Await final primary; no decrement yet. |

- **FROZEN DECISION:** reinforcement removal is a distinct action, never a
  vein-depletion MiningAttempt. `The rock is too hard to mine.` cannot be
  automatically mapped only to reinforcement because lava wall is also
  possible; context is required.
- **OPEN / DATA-NEEDED:** `You fail to produce anything here. The rock is
  stone hard.` is observed as unknown ore vein under surface during tunnelling,
  but lacks fixture evidence. It never alone creates/synchronizes context.

### PlainRock presentation information levels

**MaterialOnly PlainRock:** recognized PlainRock with no supplied Quality is a
valid Prospecting result. `PlainRock + Grade` and `PlainRock + ExactMax`
are the more precise information levels. Every level is a `ProspectingResult`, not an
`ActiveVeinContext`: it has no `VeinMaterial`, Remaining, vein confidence,
MiningAttempt binding, yield matching, or Exact Remaining decrement. Terminal
PlainRock still clears `ActiveVeinContext` and creates no replacement Vein
Context. The result may be retained per profile as transient
presentation state for `Prospecting Results`; this does not create a substitute
Vein Context.

MaterialOnly is shown as `Plain Rock` without a Quality row. A later Grade or
ExactMax replaces it with the more precise presentation result.

A newer PlainRock replaces an older presentation result. A synchronized Vein
clears it; a Vein followed by PlainRock clears the Vein Context and presents
PlainRock. A new Prospecting start alone does not clear the last PlainRock
result. Existing hard
`ClearState` paths clear both states. This transient result is per profile and
is not persisted or restored at application startup. After a previous PlainRock
result, a later TypeOnly or Partial Vein Context has presentation precedence;
the old PlainRock remains only as an internal fallback until a synchronized Vein
or clear. No stale/resurfacing PlainRock behavior was confirmed.

### Boundaries, reset, reconnect, and gaps

- **FROZEN DECISION:** hard boundaries include app start, reconnect, Logging
  started, Reset Session, StreamContinuityLost, Nearby Scan / Only rock, Surface start, Tunnelling
  start, destruction, hard material/Quality/Remaining contradiction, and an
  incompatible otherwise eligible Wall primary.
- **FROZEN DECISION:** matching Examine, Floor/Ceiling off-target Mining,
  bonuses, Analyse, queue announcements, and third-person activity are not hard
  contradictions.
- **FROZEN DECISION:** Reset Session is an ordered barrier. It clears
  MiningSessionCounter, ActiveVeinContext, ProspectingSequence, MiningAttempt,
  BreakPending phase, AnalyseSequence, and all other continuity-dependent
  per-profile state. It never resets monitor checkpoints, rewinds/replays a
  file, or restarts monitoring.
- **FROZEN DECISION:** Reset Session is the only reset that clears a
  profile's cumulative Total.
- **FROZEN DECISION / IMPLEMENTED:** Full and selective material reset share
  the same lower-level material-counter primitive. `Reset All Material Counters`
  removes all current material counters for the target profile, preserves
  cumulative Total and Vein Context, and its existing `MainViewModel` path also
  clears pending MOI. Selective material reset removes only the selected current
  material counters and preserves Total, other materials, Moment of Inspiration,
  pending MOI, Vein Context, Remaining, Nearby Scan, MiningAttempt, and
  Prospecting state. ResetWindow selective reset and Quick Reset delegate to the
  same domain operation rather than owning separate mutation logic.
- **FROZEN DECISION:** Reset Vein Context per profile uses an ordered
  `ResetProfileBarrier`; it clears only the target profile's active context,
  attempt, partial assembler state, and context continuity, while preserving
  all Mining counters and Total.
- **FROZEN DECISION:** StreamContinuityLost invalidates only continuity
  dependent state for its profile. Historical Mining counters remain unchanged
  unless a separate policy says otherwise; later lines are post-gap.
- **FROZEN DECISION:** truncation/replacement with continuity loss means EOF
  rebaseline + gap and no replay.
- **LOG-CONFIRMED:** Logging-start grammar is `Logging started YYYY-MM-DD` and
  may be unprefixed. The parser then requires the literal `Logging started `
  followed by a valid `yyyy-MM-dd` date; arbitrary suffixes are rejected. This
  does not resolve the Unknown-versus-Ambiguous policy after a boundary.
- **FROZEN DECISION:** Logging started is a hard continuity boundary.
- **LOG-CONFIRMED:** reconnect has the anonymized grammar
  `Reconnecting <player>! <server>.`.
- **FROZEN DECISION:** reconnect invalidates partial sequence and in-flight
  attempt; old context cannot remain Tracking/Synchronized without new proof.
- **OPEN / DATA-NEEDED:** after reconnect/Logging started choose deliberately
  between no context (Unknown) and an invalidated displayed context (Ambiguous).

### Minimal conceptual state and consumer isolation

The per-profile state below is conceptual, not a demand for exact class names.
Some elements are implemented today and some remain optional/future:

- `ActiveVeinContext?`;
- `LastPlainRockProspectingResult?` (transient presentation result only, not a
  Vein Context);
- `ProspectingSequence?`;
- `MiningAttempt?`, including potential BreakPending phase;
- `EligiblePlainRockResult?`, `EligibleDirectVeinContext?`, and
  `EligibleNearbySnapshot?` in their owning per-profile processors;
- `AnalyseSequence?`;
- `ObservationEpoch` / `ContinuityGeneration`;
- MiningSessionCounters in the existing per-profile/global service model.

Eligibility state is ownership-only state, separate from presentation snapshots.
It is per profile, closes on the first non-augment line and all hard continuity
boundaries, and never changes Remaining or Mining counters.

The minimal conceptual `MiningAttempt` lifecycle is: None → Active after a
confirmed action start; an informational warning leaves it Active; an actual
no-yield terminal → Completed without decrement; `The wall breaks!` →
WallBrokenAwaitingPrimary/BreakPending; a compatible successful primary →
optionally one decrement → Completed; a new hard boundary → clear/invalidate.
This does not require a specific code enum.

**IMPLEMENTED:** a recognized primary also completes the currently tracked
unbound active Wall attempt, without identifying its source or decrementing a
vein. An unbound WallBrokenAwaitingPrimary attempt closes on a primary while
preserving any later context. Bound compatibility and depletion rules are unchanged.

Mining counter parsing remains independent of vein tracking. A valid local
primary MiningEvent still increments counters when there is no context, no
compatible attempt, or continuity is Unknown; the tracker may reject a
decrement without losing the Mining count.

- **FROZEN DECISION:** on consumer `Process` failure coordinator catches the
  exception and calls `Invalidate(profile)`. If Invalidate succeeds, subsequent
  items may resume from invalid/unknown state. If it throws, fault/suppression
  is limited to that consumer/profile for future normal lines. Other consumers
  and profiles continue; control items follow coordinator contract. Prospecting
  failure never stops Mining.

### Diagnostics, privacy, and hypotheses

- **IMPLEMENTED:** `RuntimeDiagnosticService` is the controlled runtime
  diagnostics channel. It is in-memory, bounded, never-throw, and receives
  lifecycle/infrastructure events plus recognized domain events. It does not log
  every raw Event-log line or every Mining yield.
- **IMPLEMENTED:** local `DiagnosticsWindow` may retain detailed local data for
  troubleshooting. Every user export path (`Copy Selected`, `Ctrl+C`, `Copy
  All`, `Save Log`) passes through the shared `DiagnosticsExportSanitizer`;
  identifying profile data and private filesystem paths are pseudonymized or
  redacted while useful non-identifying domain data, including the Event segment
  filename, is retained.
- **FROZEN DECISION — diagnostic evidence collection:** audits 79A–79C
  investigated real `_Event` / `_Skills` availability and Event↔Skills evidence
  pairing. They confirm that more detailed Evidence Capture could be explored,
  but reveal no current diagnostic problem that requires it. The existing
  `RuntimeDiagnosticService` is sufficient. Do not implement
  `EvidenceCaptureSession`, `_Skills` tailing, Event↔Skills pairing, or expanded
  Domain Diagnostics. These audit findings are not an implementation plan; this
  topic is **CLOSED / DEFERRED** and may reopen only for a new real diagnostic
  problem.
- **FROZEN DECISION:** documentation/tests do not copy real nicknames, profile
  names, private paths, or large raw logs. Use `LocalPlayer`, `OtherPlayerA`,
  `ProfileA`, and `ServerA`; retain only minimal anonymized parser literals.
- **OPEN:** `ExternalDepletion` remains diagnostics-only domain work. It may
  compare a later authoritative observation with locally expected depletion, but
  it never mutates Mining history, `TotalCount`, or material counters and does
  not infer ownership.
- **OPEN / DATA-NEEDED:** non-decisions that may be investigated later include
  time since Prospecting as a continuity signal, physical-vein heuristics based
  on material/quality/remaining/history, passive Examine refresh, and
  same-material physical-vein identity. None may become runtime semantics without
  a separate explicit decision.

## Central open, data-needed, and deferred decisions

| Decision | Status | Blocks | Does not block |
| --- | --- | --- | --- |
| Additional Estimated parser texts | DATA-NEEDED | Unconfirmed Estimated message literals beyond currently recognized forms | Existing `EstimateBand` model and confirmed parsers |
| Exact Quality threshold | VALIDATION-NEEDED | Audit 72A observed MaterialOnly at `1.123–19.139` and Grade at `33.339–61.073`; **DATA-NEEDED** evidence is missing for `20–33.339`, `80+`, `90+`, `91+`, and ExactMax pairing. Independently validate the selected Grade-through-90 / ExactMax-above-90 contract | Quality model, Grade handling, and exact-literal parsing |
| Remaining = 0 UI/diagnostic representation | OPEN | Presentation only | `Exact(0)` retention, Tracking, non-binding, and non-negative safety |
| Unknown vs Ambiguous after reconnect | OPEN / DATA-NEEDED | Reconnect presentation policy | Reconnect boundary/invalidation |
| Unknown vs Ambiguous after `Logging started` | OPEN / DATA-NEEDED | Logging-start presentation policy | Logging-start grammar/boundary |
| RockSalt without direct Salt/Flint finalization | OPEN | RockSalt completion branch without the implemented direct-Salt variant | RockSalt literal/model/Quality and direct-Salt finalization |
| PlainRock skill correlation and unrelated later Quality | VALIDATION-NEEDED | Audit 72A found the same correlation for PlainRock and normal Veins, but exact thresholds and unrelated later Quality remain unproven | MaterialOnly, Grade, and ExactMax PlainRock results from recognized literals; no skill-based runtime logic |
| Detailed diagnostic Evidence Capture | CLOSED / DEFERRED | Audits 79A–79C found no new real diagnostic problem; do not add `EvidenceCaptureSession`, `_Skills` tailing, Event↔Skills pairing, or expanded Domain Diagnostics unless a new real diagnostic problem requires review | Existing bounded `RuntimeDiagnosticService` and diagnostics export boundary |
| Direct Flint and RockSalt Flint | DATA-NEEDED | Unimplemented augment/finalization variants | Implemented PlainRock Salt/Flint, Nearby Salt/Flint, and qualified Direct Salt tails |
| Post-destruction representation | OPEN / DATA-NEEDED | Destroyed-context UI/state form | No-further-decrement safety and attempt completion |
| Non-local vein-destruction visibility | DATA-NEEDED | Whether another player's destruction has a local Event-log signal | Current rule: never infer a signal or ownership |
| Non-local wall-break-warning visibility | DATA-NEEDED | Whether another player's warning is visible in the local Event log | Current warning semantics for local tracked attempts |
| Same-material physical-vein identity | OPEN / DATA-NEEDED | Any heuristic identity inference | Explicit complete Prospecting replacement |
| `ExternalDepletion` diagnostics | OPEN | Diagnostic model/output and physical-vein association policy | Current Mining counters and exact local decrement |
| Nearby Scan semantic mapping | DATA-NEEDED | Mapping `MaterialText`/`GradeText` into vein/quality domain models | Existing ordered Nearby Scan snapshot and cave-state invalidation |
| Mining Inspiration rarity tiers | DATA-NEEDED | Rare/Supreme/Fantastic classification | Existing strict Mining Inspiration counter |
| Monthly/daily rotation continuity and deduplication | DEFERRED | Safe cross-segment ordering/deduplication across overlap/late old-tail | Current 5-minute active Event-log window and normal Mining/Prospecting flows |
| Advanced ActiveVeinContext presentation | FUTURE | Optional styling/interaction only | Current basic per-profile Vein Context presentation |

## Rule for future domain work

Before every Prospecting/Mining domain change, read this baseline and identify
the relevant evidence, frozen decision, implementation status, and open blocker.

- A **FROZEN DECISION** changes only through an explicit, documented domain
  review.
- A **LOG-CONFIRMED** literal is empirical evidence; do not generalize beyond
  the observed grammar without evidence.
- **EXTERNAL-DOCUMENTED** information may guide investigation but does not
  silently replace real-log evidence.
- **OPEN**, **DATA-NEEDED**, and **VALIDATION-NEEDED** block only the behavior
  named by that item; they must not stop independent work.
- **DEFERRED** work remains intentionally outside the current critical path.
- Different UI entry points must delegate to existing domain operations or a
  shared primitive when they perform the same behavior; do not create parallel
  mutation mechanisms.
- On a conflict, stop the change, identify whether it is an empirical-literal
  conflict or a semantic-contract conflict, consult the appropriate source
  hierarchy, and update this baseline only by a conscious decision.

Historical implementation audits, old code maps, old test-count checkpoints,
and superseded implementation order are preserved in
`Documentation/Archive/PROSPECTING_IMPLEMENTATION_AUDIT_2026-08-22.md` and are
not current authority.
