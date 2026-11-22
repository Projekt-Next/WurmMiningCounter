> Historical document — archived for project reference. Not a current source of truth.

# Session checkpoint — 2026-08-22

## Stable runtime architecture

```text
EventLogMonitor.LineRead
├─ MiningEventMonitorBridge -> MiningEventProcessor -> MiningSessionCounter
└─ ProspectingEventLineSidecar -> EventLineCoordinator -> VeinContextProcessor
```

### Rules

- Mining Counter remains the direct, stable path.
- Prospecting operates exclusively as a sidecar.
- `EventLogMonitor` must not regain coordinator delivery.
- Do not restore `GetEarlierSegments` or cross-segment ordering to the monitor's
  live path.
- The sidecar must not block Mining Counter.
- Counter reset is immediate.
- Vein reset uses a separate sidecar barrier.
- If rotation continuity returns, it belongs to a sidecar/continuity layer.

## Live checkpoint

- Mining Counter works.
- Vein Context works concurrently.
- Live test: PASS.
- Live qualitative test: Material-only, Estimated, Exact + delta, and Mining
  Counter without regression: **PASS**.
- Live test on three profiles: **PASS**.
- Live contradiction test and post-launch checks on remaining profiles: **PASS**.
- Latest full suite: **802/802 PASS**.

## Prospecting core status

```text
PROSPECTING CORE =
FUNCTIONALLY COMPLETE FOR MATERIAL-ONLY, QUALITATIVE, AND CONFIRMED EXACT FLOWS
```

Confirmed direct exact flows, Material-only TypeOnly contexts, qualitative
Grade/EstimateBand flows, MiningAttempt lifecycle, guarded Exact Remaining
tracking, re-prospecting, hard continuity boundaries, read-only context
snapshots, and the MainWindow VeinContexts presentation are implemented.

## STEP 44 — UNIFIED PROGRESSIVE PROSPECTING AND VEIN EMERGENCE

- One progressive observation path is used for every player; skill value does
  not select a parser or pipeline.
- Precision comes exclusively from recognized Event-log messages:
  `Material -> TypeOnly`, `Material + Quality -> Partial`,
  `Material + Quality + Remaining -> Synchronized`, and guarded local Exact
  depletion -> `Tracking`.
- `A vein of pure <material> emerges here.` is timestamp-normalized, maps only
  existing `VeinMaterial`, and uses
  `ProspectingObservationUpdate -> ActiveVeinContext`.
- Emergence creates Material / Unknown Quality / Unknown Remaining / TypeOnly;
  it replaces prior context data, does not bind depletion, and does not change
  Mining counters or Total.
- Per-profile state is isolated. The live issue where only the first profile
  received emergence context is resolved; three-profile live test: PASS.

## STEP 45B — DESCRIPTIVE EMERGENCE AND CONTRADICTION LIFECYCLE

- Timestamp-normalized descriptive emergence maps the confirmed Marble and
  Slate literals to their existing `VeinMaterial`; Sandstone and RockSalt stay
  in `VeinExamineParser`.
- `ApplyMaterialObservation` is shared by Prospecting, emergence, and Examine.
  Same-material input is benign confirmation and preserves Exact/Synchronized/
  Tracking data.
- A different observed material replaces the context as TypeOnly and clears
  old Quality/Remaining. If it contradicts `BoundVeinContext`, the active
  MiningAttempt is cancelled before replacement, so it cannot later decrement
  or clear the new context, including after a wall break.
- Regression coverage: Exact Sandstone 5571 -> bound Wall -> descriptive Marble
  -> Marble TypeOnly -> shards leaves Marble intact without decrementing
  Sandstone; later Marble Prospecting upgrades TypeOnly -> Partial ->
  Synchronized.

## STEP 46A — RESET WINDOW BOTTOM ANCHOR

- ResetWindow waits for final SizeToContent layout before positioning.
- Its bottom edge remains 12 px above the screen working area; a later height
  increase moves the window upward, while MaxHeight=620 and scrolling remain
  unchanged.
- Live verification: PASS.

## STEP 40 — QUALITATIVE PROSPECTING

- Material immediately creates `TypeOnly` with Unknown Quality and Remaining;
  it neither binds a Wall attempt nor decrements.
- Quality Grade supports Poor, Acceptable, Normal, Good, VeryGood, and Utmost;
  UI displays each established quality range.
- Estimated Remaining supports Plenty, FewWeeks, StartingToDeplete,
  ProspectAnotherVein, RunOutSoon, and RunOutAnyHour; UI displays its range and
  never locally decrements it.
- `Material -> Grade -> EstimateBand` replaces TypeOnly with Synchronized.
- Exact local decrement alone emits `↓N <Remaining>` before the current value;
  quick decrements accumulate, expire after about one second, and never appear
  for re-prospecting/resync, EstimateBand, or Unknown.

## RockSalt and PlainRock

### DONE

- `You would mine stone shards here.`, `You find only rock.`, and
  `You would mine stone stone shards here.` are the three exact PlainRock
  literals.
- ExactMax Quality finalizes each PlainRock result with empty Augments, no
  Remaining, and no ActiveVeinContext; later Salt is a no-op.
- `RockSalt -> ExactMax Quality -> direct Salt` finalizes
  `ProspectingResult.Vein(RockSalt, ExactMax, Remaining.Unknown, empty
  Augments)`.
- Direct Salt is only the RockSalt finalizer in that confirmed sequence; it is
  not assigned to Augments.
- Exact Examine literal
  `A vein of crystalized sediment formed over time by evaporation.` maps to
  RockSalt.

### OPEN / DATA-NEEDED

- PlainRock without Quality.
- RockSalt without direct Salt/Flint.
- Salt/Flint association and lifecycle across direct Prospecting, NearbyScan,
  and RockSalt.

## Deferred/open domain work

- Quality/Remaining contradiction predicates.
- ExternalDepletion diagnostics.
- Post-destruction Unknown versus Ambiguous representation.
- Rotation continuity for monthly/daily overlap and late old-tail.
- Full NearbyScan semantics, including material/grade mapping.
- Analyse as a future feature.

## STEP 38C — CURRENT NEARBY SCAN AUDIT

**Status: COMPLETE — no semantic implementation made.**

- `NearbyProspectingObservation(string Description)` intentionally retains only
  the uninterpreted text between the confirmed exact outer literals `There is `
  and ` nearby.`. `TryParseNearbyScan` timestamp-normalizes its input, requires
  a non-empty description, and does not map material or grade.
- The parser and model have no production consumer. No NearbyScan observation
  reaches `ActiveVeinContext`, `VeinContextSnapshot`, the UI, counters, or
  Remaining tracking. This preserves the required absence of material/grade
  semantics, but means the current parser is dormant.
- Lines still reach `VeinContextProcessor` as normal ordered sidecar input and
  are offered to `ProspectingSequenceAssembler`. In the active state
  `RockSalt -> ExactMax`, any intervening NearbyScan line clears the
  direct-RockSalt-Salt guard; the following Salt will not finalize the confirmed
  RockSalt result. This is accidental cross-operation interference, not
  NearbyScan semantics.
- There is no test for that interference or for an ordered sidecar NearbyScan
  line being a strict no-op. The parser-only suite passes: **78/78**.

## Release-prep checkpoint

- Version 1.1.0 is prepared, without commit/tag/push in this step.
- Reset Session is global and the only operation that resets Total.
- Per-profile material-counter reset preserves Total and Vein Context.
- Per-profile Vein Context reset preserves counters and Total.
- NearbyScan full semantics/UI and Analyse remain deferred; do not infer material, grade, augments, or `ActiveVeinContext` from NearbyScan.
