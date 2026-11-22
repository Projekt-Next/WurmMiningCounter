> Historical document — archived for project reference. Not a current source of truth.

# Milestones

| Milestone | Goal and outcome | Status |
| --- | --- | --- |
| M1 — Foundation | Project structure, WPF `net8.0-windows`, and assumptions. | CLOSED |
| M2 — Initial Wurm detection | Initial installation/process detection; removed in M9 because monitoring does not need it. | CLOSED, superseded |
| M3 — Initial PlayerScanner | Initial profile scanner; removed in M9 because the monitor discovers profiles from logs. | CLOSED, superseded |
| M4 — Event monitoring | Global monitor, reader, retries, reconciliation, and shutdown. | CLOSED — PASS WITH NOTES |
| M5 — Mining parser/session pipeline | Parser for 12 materials, per-profile counter, processor, and bridge. | CLOSED — PASS WITH NOTES |
| M6 — UI integration | Composition root, Mining pipeline to UI, and status. | CLOSED — PASS WITH NOTES |
| M7 — UI / tray / Settings | Tray and Settings; simplified later in M8. | CLOSED — PASS WITH NOTES |
| M8 — Placement / simplified Settings / Single Instance | Placement, auto-save for two settings, Reset in Settings, and mutex + pipe. | CLOSED — PASS WITH NOTES |
| M9 — Cleanup, audit and 1.0 preparation | Removed unused detection/profile discovery, process smoke test, final MainWindow/placement/icon polish, and release documentation. | CLOSED — PASS WITH NOTES |
| M11.1 — Event Log pipeline prerequisite | Ordering/concurrency audit and closure: per-profile coordinator, atomic batches, continuity gaps, segment ordering, ordered reset, and consumer isolation. | CLOSED — 283/283 tests PASS |
| M11 — Prospecting | Independent sidecar, confirmed Exact and qualitative flows, Vein Context presentation, and ordered resets. Details: [Specification](../PROSPECTING_SPECIFICATION.md), [Scenarios](../PROSPECTING_SCENARIOS.md). | CLOSED FOR 1.1.0 CONFIRMED FLOWS — OPEN/DEFERRED items remain |

M2 and M3 are historical, not current runtime components. The final state is described in [Architecture](../ARCHITECTURE.md).

## Release 1.0.0

**RELEASED.** All features in the 1.0 scope are complete, the documentation matches the final code, and there are no release blockers.

- GitHub repository: `Projekt-Next/WurmMiningCounter` (private).
- GitHub Release: [`v1.0.0`](https://github.com/Projekt-Next/WurmMiningCounter/releases/tag/v1.0.0).
- Platform: Windows x64.
- 256/256 tests PASS.
- Release build: 0 warnings, 0 errors.
- Standard manual live test: PASS.
- Lite/Compressed publication tests: PASS WITH NOTES.
- No runtime errors were detected.

Prospecting and autostart are outside 1.0. Further work is listed in the [Roadmap](../ROADMAP.md).

## Hotfix 1.0.1

**RELEASED.** GitHub Release: [v1.0.1](https://github.com/Projekt-Next/WurmMiningCounter/releases/tag/v1.0.1), tag `v1.0.1`, commit `20999bfcb010dea3e27fffeb3aaf6abfc8020ada`.

- Borderless MainWindow and SettingsWindow with drag support.
- Settings positioned at the bottom-right of `WorkingArea`, with a custom `×`
  button and dynamic version display without build metadata.
- `TotalCount` in each profile header and an improved counter layout.
- Left-clicking the tray icon toggles MainWindow visibility and restores a
  minimized window.
- 259/259 tests PASS; Release build: 0 warnings, 0 errors.
- The Windows x64 release contains five assets: Standard, Compressed, Lite,
  `SHA256SUMS.txt`, and `README.txt`.

## Release candidate 1.1.0

**PREPARED, NOT YET PUBLISHED.**

- Independent Prospecting sidecar preserves the direct Mining Counter path.
- Exact, Material-only/TypeOnly, Grade, EstimateBand, Exact Remaining tracking, and Vein Context are implemented.
- Reset Session, per-profile material-counter reset, per-profile Vein Context reset, and tray ResetWindow are included.
- 775/775 tests PASS; Release build: 0 warnings, 0 errors.
