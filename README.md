# WurmMiningCounter

## Project overview

WurmMiningCounter is an independent Windows x64 companion for Wurm Online. It monitors game log files and presents per-profile Mining, Prospecting, nearby-resource, alert, and skill information. It starts monitoring existing Event logs at their current end; it does not replay historical Event entries.

## Features

- Per-profile Mining counters for 12 supported materials, plus Total.
- Progressive Prospecting results, Vein Context, Nearby Scan, and guarded local Exact Remaining tracking.
- Recognizes observed Adamantine and Glimmersteel emergence and Prospecting messages; their pickaxe sweet spots remain unavailable until their difficulties are confirmed.
- Per-profile Moment of Inspiration tracking and an independent reset.
- Unified per-profile Prospecting Results reset, available from Prospecting Results and Nearby Scan.
- Automatic state clearing when the exact `You enter the cave.` or `You leave the cave.` message is observed.
- Event Alerts with the built-in Wall Break Soon and Wall Break triggers, custom triggers, and optional sound and popup delivery.
- Live Skill Statistics, including Current, Last Gain, and Session Gain; Mining statistics include action/tick metrics, gain rate, milestones, and ETA.
- Analyse results per profile, including observed traces without compass directions and an approximate vein map.
- Tray controls for Always On Top, Alerts, Analyse, Statistics, Settings, and Exit.

## Download

Download the latest stable [GitHub Release](https://github.com/Projekt-Next/WurmMiningCounter/releases/latest). Packages are for Windows x64:

- **Compressed** (`win-x64-compressed`) — self-contained, compressed single-file package; no separate .NET Runtime installation is required. Recommended for most users.
- **Full** (`win-x64`) — self-contained, uncompressed single-file package; no separate .NET Runtime installation is required.
- **Lite** (`win-x64-lite`) — framework-dependent single-file package; requires the [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

Each runtime ZIP contains `WurmMiningCounter.exe`, the MIT `LICENSE`, this `README.md`, and an empty `Sounds/` folder. You may add your own `.wav` files directly to `Sounds/`; sound files are not bundled. Each ZIP preserves the empty `Sounds/` directory.

## Quick Start

1. Download and extract a Windows x64 package. Compressed is recommended.
2. Run `WurmMiningCounter.exe`. Lite users must install the .NET 8 Desktop Runtime x64 first.
3. For source discovery and live-monitoring behavior, see How It Works below.
4. Use the tray icon to show or hide the main window and open Alerts, Statistics, or Settings. Choose Exit from the tray menu to stop monitoring and close the application.

## How It Works

The application automatically discovers Wurm Online Standard, Wurm Online Steam, and Wurm Unlimited Steam log locations using configured discovery rules. Eligible Event sources are monitored automatically, while discovered Skills files require a valid active-writer declaration in the corresponding console log before live monitoring begins. Additional Wurm data locations can be configured in Settings.

Startup discovery and Settings > Find locations use one shared, versioned discovery service. Find locations adds newly found sources to the running monitors without restarting existing sources. Only a complete discovery snapshot can establish that a previously known physical source is absent; partial scans do not retire sources.

Event monitoring starts at the current end of each existing log, so earlier entries are not counted or replayed. New lines are read incrementally and processed in per-profile ordered lanes. Polling is nominally every 250 ms. A fresh console declaration is required after a Skills ownership change; retained Statistics remain available across source retirement.

An Event ownership conflict pauses the affected source. If discovery confirms a different owner, monitoring that source requires an application restart and begins at the current end of the log; other sources continue independently.

Skill monitoring uses `_Skills` data. Existing history may bootstrap the current Mining skill value only; it does not contribute historical gains. Live updates provide Current, Last Gain, and Session Gain. Mining session statistics use their own per-profile baseline; Reset Session establishes a new baseline for derived statistics without resetting the raw skill tracker.

Mining, Prospecting/Vein Context, and Nearby Scan use distinct state and processing paths. Their ordering guarantees are local to their respective per-profile lanes; the application does not claim a shared atomic order across independent Event and `_Skills` sources.

## Limitations

- Mining counters and transient Prospecting/Vein/Nearby state are not restored after the application restarts.
- The application does not auto-start with Windows; see the Roadmap for deferred work.
- Analyse supports confirmed shard and ore observations, including traces without direction; other unconfirmed variants remain data-needed. Some monthly/daily log rotation and cross-file continuity cases remain outside the current scope.
- Some Prospecting variants, including Direct Flint, remain data-needed; see the technical specifications.
- WurmMiningCounter is an independent, unofficial companion project and is not affiliated with or endorsed by Wurm Online.

## Documentation

- [Architecture](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/Documentation/ARCHITECTURE.md)
- [Roadmap](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/Documentation/ROADMAP.md)
- [Prospecting Domain Baseline](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/Documentation/PROSPECTING_DOMAIN_BASELINE.md)
- [Prospecting Specification](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/Documentation/PROSPECTING_SPECIFICATION.md)
- [Prospecting Scenarios](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/Documentation/PROSPECTING_SCENARIOS.md)
- [Coding Standard](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/Documentation/CODING_STANDARD.md)
- [Changelog](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/CHANGELOG.md)
- [MIT License](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/LICENSE)

Copyright (c) 2026 Projekt NEXT. See the [MIT License](https://github.com/Projekt-Next/WurmMiningCounter/blob/main/LICENSE) for the full license text.
