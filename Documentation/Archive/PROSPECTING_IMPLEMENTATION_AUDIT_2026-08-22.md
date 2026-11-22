# Prospecting implementation audit archive — 2026-08-22

## Purpose

This file preserves historical implementation snapshots removed from the active
`PROSPECTING_DOMAIN_BASELINE.md` during the controlled documentation cleanup.

The sections below are **historical evidence only**. Their words such as
`CURRENT`, `DONE`, `NOT IMPLEMENTED`, test counts, code maps, and implementation
order describe an earlier project state and must not be used as the current
runtime contract. Current domain rules remain in `PROSPECTING_DOMAIN_BASELINE.md`;
current runtime architecture belongs to `ARCHITECTURE.md`; future work belongs to
`ROADMAP.md`; delivered history belongs to `CHANGELOG.md`.

No historical content below has been rewritten to look current.

---

## Current implementation bug — first fix

**CURRENT IMPLEMENTATION BUG / FIRST FIX:** runtime Event lines may contain a
`[HH:mm:ss]` prefix. `EventLogReader` preserves that text in `EventLogLine.Text`.
`MiningEventParser` already normalizes a valid timestamp prefix, but all of the
following compare only unprefixed text:

- `ProspectingLineParser`;
- `VeinExamineParser`;
- `SessionBoundaryParser`;
- `MiningActionContextParser`.

`VeinContextProcessor` forwards raw `EventLogLine.Text` to its parsers and
assembler. Consequently a normal timestamped runtime Prospecting line may fail
to start, parse, synchronize, or establish a boundary.

The re-audit suite result was **354/354 PASS**, but existing Prospecting parser
tests predominantly use text without timestamps. Before further domain
expansion, add shared timestamp normalization and a runtime/end-to-end test
using actual `EventLogLine` values with `[HH:mm:ss]`.

## Current code map

| File | Current responsibility | Baseline gap |
| --- | --- | --- |
| `Models/VeinMaterial.cs` | Material enum, including future materials. | No PlainRock because it is not a vein material. |
| `Models/YieldKind.cs` | Ore, Shards, RockSalt yield categories. | Used by guarded Wall depletion. |
| `Models/VeinMaterialExtensions.cs` | Frozen material-to-yield mapping. | Used by MiningAttempt compatibility. |
| `Models/MiningMaterialExtensions.cs` | Primary/bonus classification and depletion-compatible yield mapping. | No additional classification gap. |
| `Models/RemainingMeasurement.cs` | Unknown and Exact, including zero. | Estimate bands remain absent. |
| `Models/QualityMeasurement.cs` | Unknown and ExactMax. | Grade and empirical exact-threshold handling absent. |
| `Models/VeinConfidence.cs` | All five confidence names. | Synchronized-to-Tracking is implemented; Ambiguous policy remains open. |
| `Models/ProspectingResult.cs` | Exact Vein result and PlainRock result. | RockSalt-without-Remaining finalization and Salt/Flint augment forms remain open. |
| `Services/ProspectingLineParser.cs` | Start, material literals including PlainRock, exact Quality/Remaining. | No timestamp normalization; RockSalt-without-Remaining finalization remains open. |
| `Services/ProspectingSequenceAssembler.cs` | Exact ordered sequence, confirmed PlainRock + ExactMax Quality, noise, new-start abandonment. | No stop, RockSalt-without-Remaining, or augment handling. |
| `Models/ActiveVeinContext.cs` | Immutable Material, Quality, Remaining, Confidence snapshot. | No diagnostic destroyed/Ambiguous representation. |
| `Models/VeinContextSnapshot.cs` / `Models/VeinContextChangedEventArgs.cs` | Immutable read-only context contract and nullable-clear event payload. | Basic MainWindow presentation and panel refinement are implemented; further styling/UI expansion remains future work. |
| `Services/VeinContextProcessor.cs` | Per-profile Prospecting/context, read-only context snapshots/change event, MiningActionContext, MiningAttempt, PlainRock context clearing, warning/rejection/no-yield transitions, WallBreak/destruction, guarded decrement, hard boundaries, and authoritative re-prospecting. | ExternalDepletion diagnostics and Quality/Remaining contradiction predicates remain unresolved. |
| `Services/VeinExamineParser.cs` | Exact Sandstone Examine text. | No shared normalization or additional confirmed Examine forms. |
| `Models/MiningActionContext.cs` | Unknown, Wall, Floor, Ceiling, Surface, Tunnelling names. | No gap in the implemented action contexts. |
| `Services/MiningActionContextParser.cs` | Timestamp-normalized Wall/Floor/Ceiling/Surface/Tunnelling starts. | No known implemented-context gap. |
| `Services/SessionBoundaryParser.cs` | Timestamp-normalized `Logging started yyyy-MM-dd` and reconnect boundaries. | Boundary representation remains clear/null until the diagnostic policy is selected. |

---

## Canonical divergence matrix

| Area | Frozen contract | Current implementation | Status | Required correction |
| --- | --- | --- | --- | --- |
| Timestamp | All runtime Event lines normalize `[HH:mm:ss]`. | Only Mining parser does. | INCORRECT / FIRST FIX | Shared normalizer plus end-to-end EventLogLine test. |
| Material literals | Exact `iron ore` / `... shards` LOG-CONFIRMED forms. | Uses `iron`, `copper`, etc. | INCORRECT / TOO NARROW | Correct dictionary. |
| Silver / Slate | LOG-CONFIRMED direct input. | Enum only. | NOT IMPLEMENTED | Add parser literals. |
| RockSalt direct Salt | LOG-CONFIRMED ExactMax-without-Remaining sequence with Salt directly after Quality. | Finalizes `ProspectingResult.Vein(RockSalt, ExactMax, Remaining.Unknown, empty Augments)`. | DONE | RockSalt without direct Salt/Flint remains OPEN. |
| PlainRock + ExactMax Quality | LOG-CONFIRMED direct result closing context only, including `You find only rock.` and `You would mine stone stone shards here.`. | All three implemented literals finalize the confirmed sequence and clear ActiveContext without creating a vein context; parser coverage includes the latter two variants. | DONE | PlainRock without Quality remains DATA-NEEDED. |
| Salt/Flint augment association | LOG-CONFIRMED literals; frozen association, finalization OPEN. | Absent. | NOT IMPLEMENTED / PARTIALLY OPEN | Parse literals independently; defer grouping branch until policy is resolved. |
| LastAugmentableProspectingResult | Conceptual association-only state. | Absent. | NOT IMPLEMENTED / PARTIALLY OPEN | Add only after association lifecycle is resolved. |
| Nearby Scan | LOG-CONFIRMED separate Prospecting surface scan. | Ordered parser/model, per-profile latest snapshot, reset lifecycle, and MainWindow presentation are implemented. Every local scan/Only rock invalidates cave context/attempt/partial Prospecting without changing counters. | DONE | Semantic material/grade mapping remains DATA-NEEDED. |
| Analyse | LOG-CONFIRMED separate operation. | Boundary parser only; no Analyse sequence or UI. | NOT IMPLEMENTED | Keep separate from Nearby Scan. |
| Stop Prospecting | Frozen partial-only termination. | Absent. | NOT IMPLEMENTED | Add parser transition. |
| Wall / Ceiling / Surface / Tunnelling | Confirmed literals and frozen action behavior. | Parsed and processed with per-context attempt rules. | DONE | — |
| MiningAttempt | One per profile; target bound at action start. | Admission, immutable binding, completion, and hard-boundary clear are implemented. | DONE | — |
| Mining transitions | Warnings/no-yield/rejection and successful-primary completion follow their context rules. | Informational warnings and PreStartRejection are no-ops; no-yield and primary completion are implemented. | DONE | — |
| StoneShards classification | Primary-for-counter, never wall-vein depletion-compatible. | MiningMaterial classification prevents depletion compatibility; incompatible bound Wall primary is a hard boundary. | DONE | — |
| Logging started | `Logging started YYYY-MM-DD`, boundary. | Parsed and integrated as clear/null continuity boundary. | DONE | — |
| Reconnect | Confirmed grammar and boundary. | Parsed and integrated as clear/null continuity boundary. | DONE | — |
| Exact decrement / Tracking | Guarded local compatible primary. | Guarded immutable decrement, Synchronized-to-Tracking, and consecutive decrements are implemented. | DONE | — |
| BreakPending / destruction | Wall-break phase and final result. | Final compatible primary optionally decrements, then clears context and attempt. | DONE | — |
| Authoritative re-prospecting | Complete Prospecting replaces context without retargeting an existing attempt. | Replaces ActiveContext as Synchronized; old attempt binding remains immutable. | DONE | — |
| Read-only ActiveVeinContext observation | Immutable per-profile snapshot and change notification without UI coupling. | `VeinContextSnapshot`, `GetActiveContextSnapshots()`, and `ActiveContextChanged` are implemented; changed contexts emit a snapshot, clear emits null, no-ops do not emit, and callbacks run outside the profile lock. | DONE | Basic presentation is implemented; further styling/UI expansion remains future work. |
| Basic ActiveVeinContext presentation | Per-profile read-only presentation of the current context. | `MainWindow` presents Vein Context inside the matching profile panel, below non-zero material counters; Material and Confidence remain visible for TypeOnly, Unknown Quality/Remaining rows are omitted, values are aligned, and Confidence is color-coded. `ActiveContextChanged` supplies live updates. | DONE | Further styling/UI expansion remains a separate future stage. |
| ExternalDepletion diagnostics | Diagnostics only; never mutates Mining history. | No diagnostic model or output. | DEFERRED | Define diagnostic channel and physical-vein association policy. |
| Quality / Remaining contradiction predicates | Hard boundary only for irreconcilable continued continuity. | No exact predicates beyond incompatible bound Wall primary. | DATA-NEEDED | Freeze predicates and fixtures before implementation. |
| Remaining = 0 | Exact(0) is retained as Tracking but is non-bindable until a new complete Prospecting. | Exact(0) does not bind a new Wall attempt; no underflow or further depletion occurs until re-prospecting. | DONE | UI/diagnostic representation remains open. |
| Post-boundary diagnostic representation | Clear/null versus Ambiguous/destroyed diagnostic snapshot. | Clear/null is used operationally. | OPEN | Select diagnostic representation policy. |

## Vein Tracking Core checkpoint

## Runtime pipeline checkpoint

### DONE

- Mining Counter is restored to the direct `LineRead` path.
- Prospecting runs as an independent sidecar.
- The sidecar does not block Mining Counter delivery.
- Sidecar FIFO and reset-barrier ordering are covered by tests.
- Live testing confirmed concurrent Mining Counter and Vein Context operation.
- Full suite: **802/802** passing; live contradiction test and post-launch
  checks on remaining profiles: **PASS**.

### OPEN / DEFERRED

- Prospecting rotation continuity after monthly/daily overlap and late old-tail
  is deferred. Any future implementation belongs to the sidecar/continuity
  layer, **not** to `EventLogMonitor`.

## Prospecting checkpoint — 2026-08-22

```text
PROSPECTING CORE =
FUNCTIONALLY COMPLETE FOR MATERIAL-ONLY, QUALITATIVE, AND CONFIRMED EXACT FLOWS
```

### DONE

- Exact Prospecting: Start -> Material -> ExactMax Quality -> Exact Remaining
  -> Synchronized.
- Unified progressive Prospecting: Material/TypeOnly, Quality/Partial, and
  Grade/EstimateBand or Exact/Synchronized flows.
- Timestamp-normalized vein emergence through the same progressive context
  path, isolated per ProfileName.
- Three confirmed PlainRock literals with Quality.
- RockSalt -> Quality -> direct Salt -> Remaining.Unknown.
- Exact RockSalt Examine.
- MiningAttempt lifecycle.
- Guarded decrement, Tracking, and post-zero non-binding.
- Authoritative re-prospecting.
- Reset, reconnect, and Logging started boundaries.
- Read-only snapshots and ActiveContextChanged.
- Vein Context inside the matching MainWindow profile panel.
- Independent ProspectingEventLineSidecar.
- Sidecar FIFO and reset barrier.
- Prospecting isolation from Mining Counter.

### OPEN / DATA-NEEDED / DEFERRED

- PlainRock without Quality.
- RockSalt without direct Salt/Flint.
- Salt/Flint association and lifecycle.
- Quality/Remaining contradiction predicates.
- ExternalDepletion diagnostics.
- Post-destruction Unknown versus Ambiguous.
- Sidecar rotation continuity.
- Analyse as a FUTURE FEATURE.

```text
VEIN TRACKING CORE =
FUNCTIONALLY COMPLETE FOR MATERIAL-ONLY, QUALITATIVE, AND CONFIRMED EXACT FLOWS
```

### DONE

- Standard Exact Prospecting.
- Wall tracking and guarded decrement.
- Tracking and consecutive decrements.
- Floor/Ceiling off-target handling.
- Surface/Tunnelling boundaries.
- Wall break and destruction cleanup.
- Reset, stream gap, reconnect, and `Logging started` boundaries.
- Authoritative re-prospecting.
- Warnings, no-yield transitions, and PreStartRejection.
- Incompatible bound-Wall primary hard boundary.
- Post-zero policy: Exact(0) remains Tracking but cannot bind a new Wall attempt.
- PlainRock + ExactMax Quality.
- No underflow or further depletion without new Prospecting.
- Basic ActiveVeinContext presentation in `MainWindow` inside the matching profile panel, with color-coded Confidence and live `ActiveContextChanged` updates.

### Blockers for full COMPLETE

- Quality/Remaining contradiction predicates — **DATA-NEEDED**.
- PlainRock without Quality finalization — **DATA-NEEDED**.
- RockSalt without Salt/Flint finalization — **OPEN**.
- Salt/Flint augment association — **OPEN**.
- ExternalDepletion diagnostics — **DEFERRED**.

---

## Safe implementation order

Further Analyse work is **DEFERRED / FUTURE FEATURE** and is outside the
current critical chain. The active priority is:

`MiningActionContext` -> `MiningAttempt` -> guarded Remaining decrement ->
`Tracking`.

1. Shared `[HH:mm:ss]` normalization.
2. Correct `Logging started YYYY-MM-DD` grammar.
3. Timestamped runtime/end-to-end `EventLogLine` test.
4. Correct exact Prospecting material literals.
5. Extend the direct-result parser only with elements independent of open
   finalization policies.
6. Implement the next independent branch from this list; defer features with
   their own OPEN item in the table above until that item is resolved.
7. Add `You stop prospecting.` partial termination.
8. Nearby Scan is implemented independently; retain only empirical mapping work.
9. Deferred Analyse future work: structural stage/strength models and markers,
   confirmed traces, exact no-trace lines, then `AnalyseSequenceAssembler`.
10. Add Wall/Floor/Ceiling/Surface/Tunnelling MiningActionContext parsing.
11. Add per-profile MiningAttempt.
12. Add warning/rejection/no-yield behavior.
13. Add guarded Exact Remaining decrement.
14. Add `Synchronized -> Tracking`.
15. Add BreakPending/destruction.
16. Add re-prospecting and ExternalDepletion diagnostics.
17. Select reconnect/LoggingStarted confidence policy.
18. Add estimated measurements.
19. Add UI/read model.
20. Add diagnostics UI/file mechanism.
