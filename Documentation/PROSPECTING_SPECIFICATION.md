# Prospecting Specification

Status: **current functional specification for WurmMiningCounter 1.8.4**.

This document defines the required behavior of the Prospecting / Vein Context subsystem. Exact evidence, frozen domain rules, literal registries, empirical notes, and unresolved evidence questions belong to `PROSPECTING_DOMAIN_BASELINE.md`. Concrete input-to-result examples belong to `PROSPECTING_SCENARIOS.md`. Runtime component boundaries belong to `ARCHITECTURE.md`.

## Status vocabulary

- **LOG-CONFIRMED** — observed in real, anonymized Event logs.
- **EXTERNAL-DOCUMENTED** — supported by external documentation such as Wurmpedia; it does not replace log evidence.
- **FROZEN DECISION** — consciously selected project semantics.
- **IMPLEMENTED** — current code and tests implement the behavior.
- **OPEN** — product/domain semantics are not yet selected.
- **DATA-NEEDED** — real empirical evidence is still missing.
- **DEFERRED** — known work deliberately postponed.
- **FUTURE** — idea outside the current scope.

## Privacy

Documentation must not contain real character names, other-player names, local profile names, Windows user names, or local filesystem paths. Examples use neutral identifiers such as `LocalPlayer`, `OtherPlayerA`, and `ProfileA`. Wurm messages that contain no personal data may be preserved verbatim when needed as parser evidence.

## Architectural contract

**FROZEN DECISION / IMPLEMENTED:** Prospecting and Mining consume independent module-local FIFO lanes fed by the Event Pipeline dispatcher. Their workers do not block source polling or one another.

```text
EventLogReader
└─ EventPipeline
   └─ FastEventDispatcher
      ├─ MiningEventLane
      │  └─ MiningEventProcessor -> MiningSessionCounter
      └─ EventLineCoordinator (Ordered Context lane)
         └─ VeinContextProcessor
```

The Ordered Context lane owns the ordered Prospecting/Vein stream, including the Mining lines required for Vein/Remaining correlation. `EventPipeline` owns source discovery, continuity, and checkpoints; it does not perform domain processing.

## Event order

**LOG-CONFIRMED / FROZEN DECISION:** physical Event-log line order is authoritative. `HH:mm:ss` timestamps are not an ordering key because multiple meaningful lines may share the same timestamp.

**IMPLEMENTED:** per-profile ordered delivery, reset controls, and `StreamContinuityLost` are handled through the Context lane FIFO. Prospecting state machines must consume that order and must not re-sort by timestamp.

Full monthly/daily rotation continuity and deduplication remain **DEFERRED**; this does not change the current ordered per-segment contract.

## Prospecting sequence

### Start

**LOG-CONFIRMED:** the hard start is:

```text
You start to gather fragments of the rock.
```

`After you finish mining you will start prospecting.` is queue information, not a Prospecting start.

**FROZEN DECISION / IMPLEMENTED:** a new Prospecting start begins a new per-profile observation and resets pending assembler accumulation for that profile. Only an assembler before its first recognized Material is an unfinished pre-Material state. A recognized Material is already a valid MaterialOnly Vein observation; it is not invalidated merely because later precision is absent. Unrelated lines may occur between recognized Prospecting lines; no timeout is used to group a sequence.

### Progressive observation

**IMPLEMENTED:** one unified progressive pipeline is used for all precision levels. Player skill is not an input to parser selection.

| Recognized information | Context produced |
| --- | --- |
| Material only | `TypeOnly` |
| Material + Quality (`Grade` or `ExactMax`) | `Partial` |
| Material + Quality + Remaining (`EstimateBand` or `Exact`) | `Synchronized` |
| Eligible local Exact decrement | `Tracking` |

Each recognized increment may update the current observation. Data from two Prospecting starts must never be mixed.

**LOG-CONFIRMED / IMPLEMENTED:** `Prospecting start → Material → action boundary`
may be a complete lower-precision MaterialOnly Vein observation: audit 73A
confirmed 13 real normal Vein sequences with no Quality or Remaining. It creates
`ActiveVeinContext(TypeOnly)` with Unknown Quality and Remaining; UI hides both
Unknown values. TypeOnly remains unbound and never participates in local Exact
Remaining decrement. Later Quality or Remaining is an optional precision upgrade,
not a prerequisite for the MaterialOnly observation to be valid.

### Standard exact sequence

**LOG-CONFIRMED:** the standard exact shape is:

```text
You start to gather fragments of the rock.
You would mine <material> here.
It has a max quality of <quality>.
You will be able to mine here <remaining> more times.
```

A complete observation is authoritative for the newly observed Vein Context and replaces the previous tracked Remaining; it does not correct personal Mining counters.

## Direct Prospecting materials

**LOG-CONFIRMED:** the following direct Prospecting inputs are confirmed:

```text
You would mine iron ore here.
You would mine copper ore here.
You would mine tin ore here.
You would mine zinc ore here.
You would mine lead ore here.
You would mine silver ore here.
You would mine gold ore here.
You would mine adamantine ore here.
You would mine glimmersteel ore here.
You would mine sandstone shards here.
You would mine marble shards here.
You would mine slate shards here.
You would mine rock salt here.
```

**LOG-CONFIRMED / IMPLEMENTED:** `You would mine gold ore here.` occurs 75 times
in ProfileA real Event logs. It follows the same Material → Quality →
Remaining/EstimateBand pipeline as every other direct vein result and establishes
a synchronized `ActiveVeinContext` with `Material = Gold`.

| VeinMaterial | YieldKind |
| --- | --- |
| Iron, Copper, Tin, Zinc, Lead, Silver, Gold, Adamantine, Glimmersteel | Ore |
| Sandstone, Marble, Slate | Shards |
| RockSalt | RockSalt |

`VeinMaterial` and `YieldKind` are separate concepts.
Gold uses the existing generic Ore wall-binding and compatible-local-primary Exact
Remaining-decrement behavior; no Gold-specific mining mechanism exists.

## PlainRock

**LOG-CONFIRMED / IMPLEMENTED:** confirmed direct PlainRock material forms include:

```text
You would mine stone shards here.
You find only rock.
You would mine stone stone shards here.
You would mine stone rock shards here.
```

`You would mine stone rock shards here.` occurs 5,861 times in ProfileA real Event
logs. PlainRock has three confirmed information levels: MaterialOnly,
`PlainRock + Grade`, and `PlainRock + ExactMax`. Audit 71A confirmed 74 unique
MaterialOnly actions: a Prospecting start, a recognized PlainRock literal, no
Quality, and an unambiguous next action boundary. MaterialOnly immediately
produces `ProspectingResult.PlainRock(Unknown, empty Augments)`; a later valid
Grade or ExactMax upgrades that result. Every PlainRock form clears any previous
active vein context and does **not** create `ActiveVeinContext` or Remaining.
The observed lower-skill association is not a pipeline rule or threshold.

Known-Quality PlainRock supports contiguous exact Salt/Flint immutable augment
replacements in physical Event-line order. MaterialOnly remains ineligible. The
first non-augment line and every reset or continuity boundary close the
per-profile ownership window; no timeout or timestamp correlation is used.

## Prospecting Results presentation

The result `You see dark dungeons.` clears the current Prospecting result and
active vein context for that profile without changing mining counters, unless a
Mining attempt is Active or WallBrokenAwaitingPrimary. In those phases it
retains its same-action informational behavior. An in-flight Prospecting sequence
is preserved and may publish its result after this line.

The logical Prospecting pipeline is:

```text
Event Log -> parser -> assembler -> ProspectingResult -> processor
```

For a Vein result, the processor updates `ActiveVeinContext`, which is then
presented in `Prospecting Results`. For a PlainRock result, the processor sets
`ActiveVeinContext = null` and retains the existing
`ProspectingResult.PlainRock` as transient presentation state, which is then
shown in `Prospecting Results`. The PlainRock state is presentation state using
the existing result, not a second domain pipeline and not a Vein Context.

`Prospecting Results` presents the last useful result per profile. A real vein
uses the existing material, Quality, Remaining, and confidence/tracking UI. A
PlainRock is displayed as `Plain Rock`; MaterialOnly hides the Quality row, and
Grade/ExactMax display Quality. It has no Remaining or vein confidence.
PlainRock has no `VeinMaterial`, never binds a
`MiningAttempt`, never participates in yield matching or Exact Remaining
decrement, and never becomes `ActiveVeinContext`.

MaterialOnly, Grade, and ExactMax PlainRock each replace an older PlainRock.
A synchronized Vein clears PlainRock presentation; Vein -> PlainRock clears
the Vein Context and presents PlainRock. A new Prospecting start alone does not
clear the last PlainRock; its next recognized PlainRock literal replaces it.
Existing hard `ClearState` boundaries clear both
states. The state is per profile and is not persisted/restored at startup.

After a PlainRock result, a later TypeOnly or Partial Vein Context takes
presentation precedence. The prior PlainRock remains only as an internal
fallback until a synchronized Vein or a clear; no stale/resurfacing PlainRock
behavior is confirmed.

## RockSalt special flow

**LOG-CONFIRMED / IMPLEMENTED — direct material aliases:** both
`You would mine rock salt here.` and the historical exact alias
`You would mine salt rock salt here.` map through the same exact material
lookup to `VeinMaterial.RockSalt`. The latter occurred once for ProfileA on
2016-12-09. It is not a new material or pipeline; no fuzzy, substring, or
general `salt` matching is used. Its observed `Grade(Poor) → Salt` flow uses
the existing RockSalt finalization below.

**LOG-CONFIRMED / IMPLEMENTED:** real RockSalt observations support:

```text
RockSalt material
→ Quality (ExactMax or Grade)
→ direct Salt
```

without a Remaining line. The direct Salt after Quality finalizes a RockSalt vein observation with `RemainingMeasurement.Unknown`: ExactMax remains ExactMax and Grade remains Grade. Salt is the direct RockSalt Prospecting finalizer, not Remaining; it does not create Exact Remaining, an estimate band, or an ordinary ore depletion band.

**LOG-CONFIRMED / IMPLEMENTED:** 72 ProfileA sequences use `RockSalt → Grade(VeryGood) → Salt`. The shared `QualityMeasurement.Grade` implementation supports Poor, Acceptable, Normal, Good, VeryGood, and Utmost; this does not claim that the other five Grades have been observed in this RockSalt flow.

**LOG-CONFIRMED:** audit 76A confirms 83 direct RockSalt observations, all
`Material → Quality → Salt`: 72 `Grade(VeryGood)`, ten `ExactMax`, and one
historical direct-material alias with `Grade(Poor)`. It found zero confirmed
MaterialOnly, Quality-without-Salt, or Salt-without-Quality sequences. This
confirms no runtime gap and adds no skill-based rule or threshold.

**DOMAIN NOTE:** player observation places the real RockSalt resource around several dozen extractions (approximately 50), but no exact limit is confirmed or encoded. Direct-Salt finalization therefore remains `Remaining.Unknown`.

**PARTIAL ISOLATION:** `RockSalt → Grade` without direct Salt remains Partial; RockSalt material-only plus Salt does not finalize. A normal `VeinMaterial → Grade` continues to await Remaining/band, and Salt is not its finalizer.

**OPEN / DATA-NEEDED:** audit 76A found no confirmed low-precision direct
RockSalt shape: MaterialOnly, Quality without Salt, or Salt without Quality.
Their finalization, including `RockSalt → Quality` without direct Salt or
Flint, remains unresolved pending real data; no rule or skill threshold may be
inferred.

## Quality measurement

**FROZEN DECISION / IMPLEMENTED:** Quality is a measurement variant, not a plain integer.

Supported forms:

- `Unknown`
- `Grade`
- `ExactMax`

Implemented Grade values and display ranges:

| Grade | Display |
| --- | --- |
| Poor | `Poor quality (20–29)` |
| Acceptable | `Acceptable quality (30–39)` |
| Normal | `Normal quality (40–59)` |
| Good | `Good quality (60–79)` |
| VeryGood | `Very good quality (80–94)` |
| Utmost | `Utmost quality (95–99)` |

Grade evidence remains qualitative; it must not be converted into a fabricated ExactMax value.

These display ranges describe qualitative Grade labels. They do not select the
numeric representation. The project Quality precision contract is Grade
through 90 (`20–59` and `60–90`) and numeric ExactMax above 90.

Quality is also continuity evidence. A confirmed hard Quality contradiction may invalidate continuity; exact contradiction predicates beyond currently frozen cases remain **DATA-NEEDED**.

**VALIDATION-NEEDED:** audit 72A found MaterialOnly at Prospecting
`1.123–19.139` and Grade at `33.339–61.073` in reliably paired real
`_Event`/`_Skills` data. **DATA-NEEDED — evidence gap:** it found no reliable
data for `20–33.339`, `80+`, `90+`, or `91+`, and no paired ExactMax result.
This is correlation only and
does not revalidate exact thresholds. The production parser does not branch on
skill: qualitative Event-log literals produce `QualityMeasurement.Grade`, and
numeric max-quality literals produce `QualityMeasurement.ExactMax`.

## Remaining measurement

**FROZEN DECISION / IMPLEMENTED:** Remaining is also a measurement variant.

Supported forms:

- `Unknown`
- `EstimateBand`
- `Exact(int)`

`Exact(0)` is valid.

Implemented estimate bands and UI ranges:

| EstimateBand | Display range |
| --- | --- |
| Plenty | `≈ 10,000+` |
| FewWeeks | `≈ 5,000–10,000` |
| StartingToDeplete | `≈ 3,000–5,000` |
| ProspectAnotherVein | `≈ 1,000–3,000` |
| RunOutSoon | `≈ 100–1,000` |
| RunOutAnyHour | `≈ 1–100` |

Boundary values are conservatively accepted by adjacent estimate bands until empirical evidence establishes exact inclusivity.

`EstimateBand` is never locally decremented. A later direct Prospecting observation replaces it with the newly observed Remaining measurement.

**EMPIRICAL PROVENANCE — audit 77B:** the currently replayable dataset is 129
`_Event` files from 2015–2018. It real-log confirms `FewWeeks`,
`StartingToDeplete`, `ProspectAnotherVein`, `RunOutSoon`, and `RunOutAnyHour`;
it has zero Exact Remaining literals. Exact Remaining remains a **HISTORICALLY
CONFIRMED** real flow from the broader 62A corpus, whose raw logs are no longer
available for replay or recount. `Plenty` has zero occurrences in the currently
available corpus and remains **DATA-NEEDED**. No runtime behavior follows from
this provenance distinction.

### Post-zero policy

**FROZEN DECISION / IMPLEMENTED:** after a guarded decrement reaches `Exact(0)`:

- `Exact(0)` remains the last `ActiveVeinContext` snapshot;
- Confidence remains `Tracking`;
- the context cannot bind a new Wall `MiningAttempt`;
- no underflow or further decrement is allowed;
- a new complete Prospecting is required before depletion tracking can resume.

The final UI/diagnostic representation of exhausted/destroyed/ambiguous state remains **OPEN**.

## Confidence

The model contains:

| Confidence | Meaning |
| --- | --- |
| `Unknown` | No reliable active context. |
| `TypeOnly` | Material is known; Quality and Remaining are not synchronized. |
| `Partial` | Material and Quality are known from the current observation; Remaining is Unknown. |
| `Synchronized` | Current observation is complete and directly synchronized from Wurm. |
| `Tracking` | An eligible local Exact decrement has occurred since synchronization. |
| `Ambiguous` | Continuity of the same physical vein cannot be safely asserted. |

**IMPLEMENTED:** TypeOnly, Partial, Synchronized, and Tracking are used by the current progressive/depletion flows. `Ambiguous` exists in the model, while the exact operational/diagnostic policy for reconnect, `Logging started`, destruction, and other uncertain boundaries remains **OPEN**; current runtime may clear the active context rather than publish an Ambiguous context.

## Vein emergence and Examine

### Vein emergence

**LOG-CONFIRMED / IMPLEMENTED:** supported emergence observations include:

```text
A vein of pure <material> emerges here.
A vein of an interlocking mosaic of carbonate crystals, otherwise known as marble, emerges here.
A vein of fine-grained, foliated, homogeneous metamorphic rock, or slate as we know it, emerges here.
```

Emergence maps only recognized `VeinMaterial` values. It uses the same material-observation path as progressive Prospecting and produces Material + Unknown Quality + Unknown Remaining + `TypeOnly`.

**IMPLEMENTED — emergence inside fresh Prospecting:** for
`Prospecting start → recognized emergence → later direct result`, emergence
retains an assembler only when the started sequence has not yet captured its
first direct Material or PlainRock. The TypeOnly emergence observation is still
applied. The later direct result then uses the existing progressive lifecycle:
the same material can synchronize the context, a contradictory direct Material
uses the existing material-observation replacement path, and terminal PlainRock
clears the emergence context without a replacement.

This exception is deliberately narrow. Emergence continues to reset the
assembler after a captured Material or PlainRock, including Material + Grade,
Material + ExactMax, and RockSalt waiting for direct Salt. New Prospecting
start, Prospecting stop, continuity loss, session/profile clear,
surface/tunnelling boundaries, and inactive-emergence behavior are unchanged.

**LOG-CONFIRMED / IMPLEMENTED:** real-log audit 62A found 25 complete B4 shapes
with this transition, plus one PlainRock + Grade overlap already covered by B1:

```text
ProfileA: 3 × PlainRock + ExactMax; 3 × Iron; 1 × Copper
ProfileB: 18 × Copper + Grade + FewWeeks
```

Regression coverage verifies Iron with ExactMax and Exact Remaining, Copper
with Grade and FewWeeks, and PlainRock with ExactMax or Grade after emergence.
The same narrow condition applies to every recognized `VeinEmergenceParser`
form, including the descriptive Marble and Slate forms, as a consequence of
their shared lifecycle; those forms are not asserted as B4 real-data cases. The
grade used by the technical Copper regression is not asserted as the Grade
observed in the 18 ProfileB records. The archived logs are unavailable here,
so this is verified transition coverage rather than a physical post-fix replay
of all 26 sequences.

**LOG-CONFIRMED / IMPLEMENTED:** `A vein of pure gold emerges here.` occurs 16
times in real Event logs and resolves through this existing grammar to Gold
`TypeOnly`; it does not invent Quality or Remaining.

Emergence does not change Mining counters or Total and does not itself decrement Remaining.

### Examine

**LOG-CONFIRMED / IMPLEMENTED:** Examine may provide vein material without a Prospecting sequence. Confirmed mappings include Sandstone and RockSalt; other implemented mappings must remain evidence-based.

RockSalt example:

```text
A vein of crystalized sediment formed over time by evaporation.
```

Examine:

- never changes Mining counters or Total;
- never decrements Remaining;
- same-material Examine is benign confirmation and preserves stronger Exact/Synchronized/Tracking data;
- different-material Examine replaces current context with new Material + Unknown Quality + Unknown Remaining + `TypeOnly`;
- cannot prove that two same-material observations refer to the same physical tile.

Additional exact Examine literals must not be invented; unconfirmed materials remain **DATA-NEEDED**.

## Material-observation contradiction lifecycle

**FROZEN DECISION / IMPLEMENTED:** direct Prospecting, vein emergence, and Examine share the material-observation lifecycle.

- same material: preserve stronger existing Quality/Remaining/confidence where the observation is only confirmation;
- different material: invalidate incompatible bound attempt first, then replace context with the new Material-only/TypeOnly observation;
- an old immutable attempt must never retarget to the new context or later decrement/clear it.

Further Quality/Remaining contradiction predicates and same-material physical-vein identity heuristics remain **DATA-NEEDED**.

## Nearby Scan

**LOG-CONFIRMED / IMPLEMENTED:** Nearby Scan is a separate Prospecting surface result using the outer grammar `There is ... nearby.`. `There is only rock nearby.` is the empty/OnlyRock form.

The current model preserves:

- raw description;
- ordered entries;
- duplicate entries;
- material text;
- optional grade text;
- `IsOnlyRock`.
- ordered Salt/Flint augments when the confirmed contiguous tail is present.

Nearby Scan:

- is stored per profile as the latest scan;
- is independent from `ActiveVeinContext` Remaining/depletion;
- invalidates underground cave Vein Context, active Vein `MiningAttempt`, and partial Prospecting as location evidence;
- does not reset Mining counters, Total, or Moment of Inspiration;
- is cleared by session/profile continuity resets according to the current runtime lifecycle;
- is also cleared only for the matching profile by Ceiling start, while preserving
  the existing Vein Context, and after a synchronized direct Prospecting
  observation has established/updated that context. Prospecting start alone, an
  assembler waiting for its first Material, and a MaterialOnly Vein observation
  do not clear it.

**DATA-NEEDED:** semantic mapping of Nearby Scan `MaterialText`/`GradeText` into `VeinMaterial`/`QualityMeasurement` remains deliberately unimplemented.

Nearby Scan is **not Analyse**.

Both ordinary and OnlyRock snapshots can receive contiguous exact Salt/Flint
immutable replacements. Duplicate augments are no-ops and preserve first-seen
order. The owner is per profile, separate from the displayed snapshot, and is
closed by the first non-augment line or a normal hard boundary. The UI does not
yet display augments.

## Contextual Prospecting augments

The physical ordered Event stream is authoritative. A known-Quality PlainRock
or Nearby snapshot accepts a contiguous exact Salt/Flint tail. A finalized
normal, non-RockSalt Direct Vein accepts a contiguous exact Salt tail only when
it has ExactMax Quality and known Remaining. Replacements preserve the existing
result fields and add augments immutably in first-seen order; duplicates are
no-ops. Any unsupported or non-augment line closes the relevant owner before
normal processing, as do reset, reconnect, `Logging started`, continuity loss,
and consumer invalidation. These upgrades do not change Mining counters,
Remaining, or MiningAttempt behavior, and use neither timeout nor timestamp
heuristics. Direct Flint and RockSalt Flint remain **DATA-NEEDED**.

## Analyse

**LOG-CONFIRMED / IMPLEMENTED:** Analyse is a separate action with its own sequence and trace grammar. It is not parsed as Nearby Scan or direct Prospecting. Confirmed shard/ore boundaries, trace and no-trace observations, per-profile assembly and a results window are implemented; unconfirmed variants remain data-needed in the domain baseline.

Gold traces observed through Analyse remain Analyse observations, distinct from
the independently confirmed direct Gold Prospecting literal.

## Personal Mining counters vs Vein Remaining

**FROZEN DECISION / IMPLEMENTED:** personal Mining counters answer “how much did the local player mine?”. Vein Remaining answers “what is the current observed/tracked state of the world resource?”. They are independent.

- only local recognized Mining results change personal counters;
- Prospecting never increments Mining materials or Total;
- re-prospecting never corrects historical Mining counters;
- external/unobserved depletion must never be added to personal counts.

## Primary, byproduct, and TotalCount

**FROZEN DECISION / IMPLEMENTED:** `TotalCount` counts successful local **PRIMARY** Mining yields, not the sum of material counters.

PRIMARY:

- StoneShards
- Ore
- Shards
- RockSalt

BYPRODUCT:

- Salt
- Flint
- SourceCrystal
- Sapphire
- Diamond
- Emerald
- Ruby
- Opal

A primary increments its material and `TotalCount`. A byproduct increments only its own material. Byproducts never decrement Remaining.

`You mine some rock salt.` followed by `You mine some salt.` therefore produces RockSalt `+1`, Salt `+1`, and Total `+1`.

`SourceCrystal` is treated as a v1 byproduct for both exact local recognized literals (`You mine some pink crystals.` and `You get a source crystal.`); no inference is made about the actual game-action source of either line.

## Mining action context

**LOG-CONFIRMED / IMPLEMENTED:** action context is explicit and per profile.

Current contexts:

- `Unknown`
- `Wall`
- `Floor`
- `Ceiling`
- `Surface`
- `Tunnelling`

Wall Mining variants forwards/upwards/downwards and their real exact aliases
`You start to mine up.` (17,734 observed occurrences) and `You start to mine
down.` (4,404) are depletion-eligible. Every alias maps to
`MiningActionContext.Wall`; no other Mining-start aliases are implemented.
Floor and Ceiling are off-target relative to an active wall vein: their primary
yield is counted but must not decrement that wall vein Remaining.

Ceiling start preserves the existing wall `ActiveVeinContext`, creates an
unbound Ceiling attempt, and clears only the matching stale Nearby Scan. Surface
Mining remains the hard boundary for underground Vein continuity.

Bare `You start to mine.` is Surface Mining. `You start to tunnel.` is Tunnelling. Both are hard invalidation boundaries for underground Vein state and do not create a Vein `MiningAttempt`.

Queue lines beginning `After you finish ...` do not start an action.

## MiningAttempt and Exact decrement

**FROZEN DECISION / IMPLEMENTED:** a per-profile `MiningAttempt` binds an immutable snapshot of the eligible Vein Context at action start. It must never retarget after later context changes.

A Wall attempt binds only when the active context is eligible, including synchronized/tracking Exact data required by current runtime rules. Material-only, Partial, Estimated, Unknown, and post-zero non-bindable contexts must not be treated as a safe Exact depletion target.

`Remaining--` requires all of:

- local successful primary result;
- an active bound eligible attempt;
- Exact Remaining greater than zero;
- compatible action context;
- compatible `YieldKind`;
- no byproduct-only result.

The decrement occurs at most once for one qualifying primary yield and changes `Synchronized` to `Tracking`.

Pre-start rejection, informational warning, and actual no-yield terminal are distinct states. Rejections do not create depletion. Informational warnings such as `The wall will break soon.` keep the attempt active and do not decrement. Confirmed no-yield terminal messages close the attempt without depletion.

A recognized primary result also completes an unbound active Wall attempt without
assigning a material or decrementing Remaining. After an unbound wall break, a
primary closes the pending attempt without changing a subsequently observed vein
context. This is a lifecycle rule for the currently tracked attempt; it does not
establish the physical source of the yield.

## Wall break and destruction

**LOG-CONFIRMED / IMPLEMENTED:** `The wall breaks!` does not itself decrement Remaining.

The active Wall attempt enters the wall-broken-awaiting-primary phase. The following compatible final primary may perform at most one final guarded decrement. After that final result the old attempt/context is ended/cleared according to the destruction lifecycle. No double decrement is permitted.

## Local Exact tracking and UI

**IMPLEMENTED:** after an explicitly signalled local Exact decrement, the current Exact Remaining is updated immediately and the profile UI may show a short transient delta indicator:

```text
↓N <current Remaining>
```

The delta accumulates only for the short local-decrement series and disappears after its transient timeout. It is not shown for re-prospecting, resynchronization, replacement, clear, `EstimateBand`, or `Unknown`.

The former `~N` representation is **not** the current UI contract. Conceptually, tracked Remaining may differ from world state because other players can mine the same resource, but the current UI uses the explicit transient `↓N` indicator for locally observed Exact decrements.

## Re-prospecting and ExternalDepletion

**FROZEN DECISION / IMPLEMENTED:** every complete new Prospecting observation is authoritative and replaces the prior tracked Remaining. It never edits personal Mining history.

Conceptual example:

```text
Prospecting: 1000
local eligible Mining: 5
local tracked state: 995
next Prospecting: 987
```

The local player still mined only 5. The extra difference is unobserved/external depletion or continuity uncertainty; it must not become Ore/Shards/RockSalt count or Total.

**OPEN:** an `ExternalDepletion` diagnostic model/output remains future work. It must remain diagnostic only unless a later explicit design decision says otherwise, and it cannot by itself prove physical-vein identity or identify another player.

## Hard continuity boundaries

**FROZEN DECISION / IMPLEMENTED:** continuity-dependent state is invalidated on hard boundaries including:

- application/runtime start;
- reconnect;
- `Logging started`;
- Reset Session;
- `StreamContinuityLost`;
- Nearby Scan / `There is only rock nearby.`;
- Surface Mining start;
- Tunnelling start;
- confirmed hard material contradiction;
- incompatible otherwise-eligible bound Wall primary;
- destruction.

Partial Prospecting, active `MiningAttempt`, wall-break pending phase, and unsafe old Vein continuity must not cross these boundaries.

The exact `Unknown` versus `Ambiguous` representation after reconnect, `Logging started`, destruction, and related uncertainty remains **OPEN**. Operational safety does not depend on choosing an Ambiguous UI representation; current runtime may clear/null the context.

## Reset semantics

### Reset Session

**IMPLEMENTED:** Reset Session enqueues local FIFO reset controls to Mining and Context. It clears session counters and continuity-dependent Prospecting/Vein state at each lane's ordered boundary. Client pause/restart alone does not reset state; reset does not restart polling or replay history.

### Full material-counter reset

**IMPLEMENTED:** per-profile `Reset All Material Counters` removes all current material counters, preserves Total and Vein Context, and retains its established pending-MOI clear behavior in the application layer.

### Selective material reset

**IMPLEMENTED:** selective reset removes only the chosen current `MiningMaterial` entries through the shared material-reset primitive. It preserves:

- Total;
- other materials;
- Moment of Inspiration and pending MOI;
- Vein Context;
- Remaining;
- Nearby Scan;
- MiningAttempt;
- Prospecting state.

`ResetWindow` selective reset and MainWindow Quick Reset both delegate to the same `MainViewModel.ResetMaterials(...)` / `MiningSessionCounter.ResetMaterials(...)` path. They must not implement separate counter mutation.

### Vein Context reset

**IMPLEMENTED:** per-profile Vein Context reset clears continuity-dependent Prospecting/Vein state through the Ordered Context lane while preserving personal material counters and Total.

### Reset Prospecting Results and cave transitions

**IMPLEMENTED:** `Reset Prospecting Results` is a shared per-profile action available from the `Prospecting Results` and `Nearby Scan` headings. It uses the existing Ordered Context reset barrier to clear Vein Context, the retained PlainRock result, pending Prospecting assembly/attempt state, and Nearby Scan state for that profile. It preserves Mining counters, Total, Moment of Inspiration, and other profiles.

**IMPLEMENTED:** the exact normalized messages `You enter the cave.` and `You leave the cave.` are additional per-profile boundaries. Each clears the same transient Prospecting/Vein and Nearby state synchronously while the ordered Context consumer processes the line. They do not reset Mining counters, Total, Moment of Inspiration, or state belonging to another profile. No partial or approximate message matching is used.

## Profile isolation

**FROZEN DECISION / IMPLEMENTED:** Prospecting sequence, ActiveVeinContext, Quality, Remaining, Confidence, Mining action/attempt state, Nearby Scan, and continuity barriers are isolated per `ProfileName`. Lines from two profiles must never be merged into one observation or attempt.

## Diagnostics integration

**IMPLEMENTED:** recognized Prospecting-domain transitions are available through Runtime Diagnostics without logging every raw Event-log line. Diagnostics may include Prospecting Start/Material/Quality/Remaining, `VeinContextUpdate`, clear reasons, Nearby Scan, lifecycle, warnings, and errors.

Diagnostics must not change parser/domain semantics. Local diagnostics may retain detailed local troubleshooting data; Copy/Save exports are sanitized at the diagnostics export boundary as defined by `ARCHITECTURE.md`.

`ExternalDepletion` diagnostics remain **OPEN** despite the general Diagnostics infrastructure being implemented.

**CLOSED / DEFERRED — diagnostic evidence collection:** audits 79A–79C confirm
that detailed `_Event` / `_Skills` evidence capture can be investigated when
needed, but no current diagnostic problem requires it. `RuntimeDiagnosticService`
is sufficient. `EvidenceCaptureSession`, `_Skills` tailing, Event↔Skills pairing,
and expanded Domain Diagnostics are not current implementation work. The audit
findings are not an implementation plan and may be reconsidered only for a new
real diagnostic problem.

## Open / data-needed / deferred work

The following are intentionally not described as current implementation:

| Status | Topic | Current boundary |
| --- | --- | --- |
| VALIDATION-NEEDED | Exact Quality threshold | Audit 72A observed MaterialOnly at `1.123–19.139` and Grade at `33.339–61.073`; **DATA-NEEDED** evidence is missing for `20–33.339`, `80+`, `90+`, `91+`, and ExactMax pairing. Independently validate the selected Grade-through-90 / ExactMax-above-90 contract; parser classification remains literal-driven. |
| DATA-NEEDED | Additional exact Estimated/Examine literals | Extend parsers only from real evidence. |
| DATA-NEEDED | Quality/Remaining contradiction predicates | Freeze exact hard-contradiction rules before adding more invalidation logic. |
| OPEN / DATA-NEEDED | Same-material physical-vein identity | Event logs provide no tile ID; time/material similarity is not proof. |
| OPEN / DATA-NEEDED | Low-precision or non-Salt RockSalt finalization | Audit 76A confirms 83 `Material → Quality → Salt` observations (72 Grade(VeryGood), ten ExactMax, one historical Grade(Poor) alias) and zero confirmed MaterialOnly, Quality-without-Salt, or Salt-without-Quality shapes. Current direct-Salt branch is implemented; other finalization remains unresolved without a skill-based inference. |
| VALIDATION-NEEDED | PlainRock skill correlation and unrelated later Quality | Audit 72A confirms correlation only; do not infer thresholds or an unrelated Quality misassociation before an existing sequence boundary. |
| DATA-NEEDED | Direct Flint and RockSalt Flint | Current deterministic augment support is limited to PlainRock Salt/Flint, Nearby Salt/Flint, and qualified Direct Salt. |
| OPEN | Post-boundary `Unknown` vs `Ambiguous` representation | Operational invalidation is implemented; representation remains selectable. |
| OPEN | Remaining `Exact(0)` / destroyed-context UI representation | Safety/non-binding is implemented; presentation remains separate. |
| OPEN | ExternalDepletion diagnostics | Diagnostic only; must not mutate personal Mining history. |
| CLOSED / DEFERRED | Detailed diagnostic Evidence Capture | Audits 79A–79C found no current real diagnostic problem. Do not add `EvidenceCaptureSession`, `_Skills` tailing, Event↔Skills pairing, or expanded Domain Diagnostics. |
| DATA-NEEDED | Nearby Scan semantic mapping | Raw material/grade text remains intentionally separate from VeinMaterial/QualityMeasurement. |
| DATA-NEEDED | Non-local wall-warning/destruction visibility | Do not infer local visibility or ownership without real logs. |
| DATA-NEEDED | Mining Inspiration rarity tiers | MOI alone must not be labeled Rare/Supreme/Fantastic. |
| DATA-NEEDED | Additional Analyse variants | Additional trace variants, materials and no-trace wordings, and interruption grammar need real-log evidence. |
| DEFERRED | Monthly/daily rotation continuity and deduplication | Belongs to the Event Pipeline source-continuity layer; Event Pipeline does not scan or merge historical segments. |

## Non-goals of this specification

This document does not preserve release history, old implementation plans, superseded UI ideas, or milestone checkpoints. Those belong to `CHANGELOG.md` or archived historical documentation. It also does not redefine literal evidence stored in the canonical domain baseline.
