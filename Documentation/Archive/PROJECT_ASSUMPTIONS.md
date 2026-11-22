> Historical document — archived for project reference. Not a current source of truth.

# WurmMiningCounter 1.0 Project Assumptions

Current technical source of truth: [Architecture](../ARCHITECTURE.md). This document records the concise final 1.0 assumptions, milestone status, and accepted technical debt.

## Final 1.0 assumptions

- The application is a small, lightweight WPF `net8.0-windows` program.
- Monitoring starts from `%UserProfile%\wurm\players`, located by `PlayerDataDirectoryLocator`.
- If this directory is missing, the status is `Monitoring unavailable`.
- The application does not detect the Wurm installation or process.
- One global `EventLogMonitor` handles all profiles.
- Profiles are derived from the `players\<profile>\logs\` structure; there is no `PlayerScanner`.
- Only `_Event.YYYY-MM.txt` and `_Event.YYYY-MM-DD.txt` are supported.
- Existing logs are initialized at EOF on startup; there is no historical backfill.
- New session files are read from the beginning; reconciliation runs every 10 seconds and after watcher errors.
- The reader supports strict UTF-8, partial lines, BOM, CRLF, retries, truncation, replacement, and SHA-256 fingerprints.
- The Event Log pipeline uses `EventLineCoordinator`: ordered items are serialized per profile, monitor batches are atomic, and timestamps do not establish order.
- Truncation/replacement, late old-tail, and ambiguous segment rotation produce ordered `StreamContinuityLost` rather than replay or guessed ordering.
- Reset Session passes through ordered monitor and coordinator barriers; it does not reset the monitor or checkpoints.
- Mining covers 12 materials; `Ore` and `Shards` remain generic.
- Each recognized event adds `+1`; counters are per-profile, thread-safe, and in-memory only.
- A profile appears in the UI only when it has a Mining result; zero values are hidden.
- Reset Session is available only in Settings, requires confirmation, and does not restart the monitor or checkpoints.
- Settings contain only `AlwaysOnTop` and `StartMinimizedToTray`, use auto-save, and have no Save/Cancel controls.
- When the tray is available, the window's X hides it; without the tray, X exits the application.
- `WindowPlacementService` saves and validates `Left`, `Top`, and `Width`; height follows the dynamic layout and is not persisted. Older JSON containing `WindowPlacement.Height` remains readable, but that field is ignored.
- `SingleInstanceService` uses a mutex, named pipe, and `ACTIVATE`.
- There is no autostart, persistent Mining history, database, export, notifications, sound, modules, or Prospecting.

The detailed specification for planned Prospecting (status: DESIGN / ANALYSIS IN PROGRESS, implementation not started at the time of this archive) is in [Prospecting Specification](../PROSPECTING_SPECIFICATION.md) and [Prospecting Scenarios](../PROSPECTING_SCENARIOS.md).

## Status milestone

| Milestone | Status |
| --- | --- |
| M1 — Foundation | CLOSED |
| M2 — Initial Wurm detection | CLOSED, later removed from runtime |
| M3 — Initial PlayerScanner | CLOSED, later removed from runtime |
| M4 — Event monitoring | CLOSED — PASS WITH NOTES |
| M5 — Mining parser/session pipeline | CLOSED — PASS WITH NOTES |
| M6 — UI integration | CLOSED — PASS WITH NOTES |
| M7 — UI / tray / Settings | CLOSED — PASS WITH NOTES |
| M8 — Placement / Settings / Single Instance | CLOSED — PASS WITH NOTES |
| M9 — Cleanup and 1.0 preparation | CLOSED — PASS WITH NOTES |
| M11.1 — Event Log pipeline prerequisite | CLOSED — 283/283 tests PASS |

## State after M11.1

- The Event Log pipeline prerequisite for Prospecting is closed: per-profile ordering, atomic batches, retry/reconciliation, EOF rebaseline, rotation/gap handling, ordered reset, and consumer isolation.
- 283/283 tests PASS.
- Prospecting is not yet implemented, but is marked **READY TO IMPLEMENT**.

## Release 1.0.0

**Status: RELEASED.**

- All features in the WurmMiningCounter 1.0 scope are complete.
- Documentation is aligned with the final code.
- There are no release blockers.
- GitHub repository: `Projekt-Next/WurmMiningCounter` (private).
- GitHub Release: [`v1.0.0`](https://github.com/Projekt-Next/WurmMiningCounter/releases/tag/v1.0.0) for Windows x64.
- 256/256 tests PASS.
- Release build: 0 warnings, 0 errors.
- Standard manual live test: PASS.
- Lite/Compressed publication tests: PASS WITH NOTES.
- No runtime errors were detected.
- Prospecting and autostart are outside 1.0. Further features are listed in the [Roadmap](../ROADMAP.md).

## Accepted technical debt

- `EventLogMonitor.Start()` performs a synchronous initial scan.
- The UI refreshes its snapshot every second.
- `SettingsService.Save()` does not propagate save errors to the UI.
- There are no deterministic tests for permission errors during directory enumeration.
- There is no complete automated tray-failure test.
- The fingerprint covers at most 4096 bytes before the read position.
- `SendActivate()` uses a short synchronous retry.
- There is no diagnostic channel for `LineRead` subscriber errors.

## Release 1.0.1

**Status: RELEASED.** GitHub Release: [v1.0.1](https://github.com/Projekt-Next/WurmMiningCounter/releases/tag/v1.0.1), tag `v1.0.1`, commit `20999bfcb010dea3e27fffeb3aaf6abfc8020ada`, Windows x64.

- MainWindow and SettingsWindow are borderless; their custom headers support dragging.
- SettingsWindow opens at the bottom-right of the current monitor's `WorkingArea`, with a 12 DIP margin, and has a custom `×` button.
- Settings displays the dynamic version from `AssemblyInformationalVersionAttribute`, without build metadata after `+`.
- Each profile has a computed `TotalCount`, displayed as a number in the section header.
- A single left-click on the tray icon toggles MainWindow visibility; a minimized window is restored.
- 259/259 tests PASS.
- Release build: 0 warnings, 0 errors.
- The release contains exactly five assets: Standard, Compressed, Lite, `SHA256SUMS.txt`, and `README.txt`.
