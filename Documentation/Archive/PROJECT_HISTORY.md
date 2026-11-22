> Historical document — archived for project reference. Not a current source of truth.

# Project history

WurmMiningCounter began as a small Mining counter for Wurm Online. The core decision was to count the current session rather than build a history.

## Monitoring instead of history

One global `EventLogMonitor` was selected for the `players` directory. Read state is kept per file to support multiple profiles and clients. Existing logs start at EOF so history is not counted after startup.

The `_Event.YYYY-MM.txt` format and daily `_Event.YYYY-MM-DD.txt` variant were identified in a real log. The rule was centralized in `EventLogFileNameMatcher`.

## Mining separation

The pipeline was split into a stateless parser, thread-safe counter, and monitor bridge. The parser knows nothing about the UI, and the counter knows nothing about files or WPF. `Ore` and `Shards` remain generic in the absence of Prospecting evidence.

## UI and tray

Initially, the UI was intended to show empty profiles. The final behavior shows a profile only after its first Mining event. The tray keeps monitoring active when the window is hidden.

The first Settings version included `HideOnClose` and Save/Cancel. It was replaced: X behavior depends on tray availability, Settings use auto-save and contain only Always On Top and Start Minimized To Tray. Reset Session was moved exclusively to Settings and confirmation was added. Autostart was deliberately excluded from 1.0.

## Placement and single instance

Window bounds with screen validation and single-instance behavior based on a mutex and `ACTIVATE` pipe were added. No legacy, takeover, or protocol versioning was added.

Final placement persistence was limited to `Left`, `Top`, and `Width`. Height is fully dynamic through `SizeToContent="Height"`; older settings containing `Height` are ignored for compatibility. MainWindow received a compact layout, a colored status dot with a tooltip, and the shared final `Resources/WurmMiningCounter.ico` icon for the EXE, window, and tray.

## Cleanup and 1.0 publication

`WurmInstallationDetector`, `WurmRuntimeService`, `WurmInstallationDetectionResult`, `PlayerScanner`, and `PlayerProfile` were removed as unused. `PlayerDataDirectoryLocator` remains for `%UserProfile%\wurm\players`.

Version 1.0.0 was published as GitHub Release `v1.0.0` in the private `Projekt-Next/WurmMiningCounter` repository. Windows x64 variants were provided: Compressed (recommended), Standard, and Lite.

## Hotfix 1.0.1

After release 1.0.0, the chrome of both windows was simplified: MainWindow and SettingsWindow became borderless while retaining drag and resize support. Settings received a custom `×` button, positioning at the bottom-right of the current monitor's work area, and a dynamic version string without build metadata.

Computed `TotalCount` was added to profile sections as the sum of current materials, not as separate state. The header and counter-column layout was adjusted. A single left-click on the tray icon toggles window visibility, and a minimized window is restored.

The hotfix was published as WurmMiningCounter 1.0.1: tag `v1.0.1` points to commit `20999bfcb010dea3e27fffeb3aaf6abfc8020ada`, and the GitHub Release provides Windows x64 Standard, Compressed, and Lite variants, along with `SHA256SUMS.txt` and `README.txt`.

Prospecting was outside the 1.0 release scope; its implementation is described by the 1.1.0 section below.

## Prospecting analysis after 1.0.1

Real Event logs from 2026-06, 2026-07, and 2026-08, along with earlier project logs, were analyzed and compared with Wurmpedia Mining. A separate specification was chosen before implementation to preserve the independence of the existing Mining pipeline and first resolve event ordering, vein state, and boundary cases.

This analysis became the source for the implemented 1.1.0 confirmed flows. Open and deferred work remains documented in [Prospecting Specification](../PROSPECTING_SPECIFICATION.md) and [Prospecting Scenarios](../PROSPECTING_SCENARIOS.md).

## M11.0b–M11.1: Event Log pipeline prerequisite

Before Prospecting began, Event-log ordering and concurrency were audited and the minimal ordered-pipeline architecture was finalized. `EventLineCoordinator` serializes items per profile, accepts atomic batches from the monitor, and isolates consumer failures.

On truncation or replacement, the reader and monitor propagate `StreamContinuityLost` rather than replay, establish an EOF rebaseline, baseline historical segments, and preserve `old-tail -> new-head` rotation order. A late old-tail and ambiguous monthly/daily segment overlap result in `RotationOrderLost` rather than guessed ordering.

Reset Session was linearized relative to source commits through the monitor processing gate and coordinator barrier. Direct, deterministic coordinator tests were added for batches, per-profile ordering, profile independence, reset, and consumer failure isolation. This became the ordering foundation used by the 1.1.0 sidecar.

## Release candidate 1.1.0

1.1.0 implements Prospecting as a sidecar and keeps the direct Mining Counter path independent. It adds Exact, Material-only/TypeOnly, and qualitative Grade/EstimateBand results, guarded Exact Remaining tracking with the transient `↓N` indicator, and Vein Context in the profile panel.

The release candidate also makes Total cumulative and independent from current material counters. Reset Session remains the only Total reset. Per-profile material-counter reset preserves Total and Vein Context; per-profile Vein Context reset preserves counters and Total. Tray `Reset...` provides selection, confirmation, dynamic sizing, Settings-style placement, and custom chrome.

Full NearbyScan semantics/UI, Analyse, rotation continuity, ExternalDepletion diagnostics, and other explicitly OPEN/DEFERRED domain items are not claimed as complete.
