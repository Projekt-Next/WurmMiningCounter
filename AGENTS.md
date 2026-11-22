# WurmMiningCounter — Agent Instructions

## Core principles

* Apply **Stability → Performance → Simplicity → Minimal I/O** when implementing, refactoring, auditing, and proposing changes. Preserve data correctness and proven behavior; every change must justify its complexity and runtime cost and improve the application.

* Implement only necessary functionality and fixes for demonstrated practical problems. Avoid unnecessary layers, state machines, and defenses against extremely rare hypothetical scenarios; remove obsolete or redundant code.

* Keep hot paths fast: avoid unnecessary I/O, allocations, locks, and polling.

* Work incrementally and stay strictly within the requested scope.

* Prefer the smallest correct change over broader redesigns or speculative abstractions.

* Preserve existing architecture, contracts, module boundaries, and established project decisions.

* Do not modify unrelated code, tests, documentation, or configuration.

* Reuse existing infrastructure only when its responsibility genuinely matches the task; otherwise keep new functionality local to its module.

## Evidence and domain behavior

* Base Wurm log behavior on confirmed real log evidence and documented project rules.

* Do not invent literals, formats, ordering guarantees, correlations, or game mechanics.

* Do not infer ordering between independent log streams unless explicitly proven.

* When evidence is insufficient, report `NOT OBSERVED` or `DATA-NEEDED` instead of guessing.

## Implementation quality

* Maintain backward compatibility unless the task explicitly changes an existing contract.

* Treat lifecycle, concurrency, I/O continuity, and failure handling as correctness-sensitive areas.

* Do not hide failures, silently recover, rebaseline, replay, or introduce heuristics unless that behavior is explicitly part of the contract.

* Keep MiningStatistics isolated from Mining/Prospecting and the Event pipeline unless integration is explicitly requested.

## Wurm log pipeline

* Uwzględniaj [wspólne zasady pipeline'u Wurm](Documentation/WURM_LOG_PIPELINE_PRINCIPLES.md) przy pracach nad Discovery, Monitoringiem i Dystrybucją.

## Validation and audits

* Validate changes with relevant targeted tests, Release build with warnings-as-errors, and `git diff --check`.

* Run the full test suite when closing a larger logical package or after changes with broad impact.

* Do not repeat expensive validation after documentation-only changes when the previous code baseline is still valid.

* Concurrency tests must exercise real interleavings deterministically; avoid timing-dependent sleeps.

* Audits are read-only unless explicitly instructed otherwise.

* Report findings as concrete `BLOCKER`, `NON-BLOCKING`, `TEST GAP`, or `NOT OBSERVED` issues. Avoid speculative or cosmetic findings.

## Documentation

* Documentation must describe the actual implemented state.

* Clearly distinguish implemented behavior from planned or deferred work.

* Update `CHANGELOG.md` under `[Unreleased]` unless performing an explicit release.

* Do not rewrite existing release history without explicit instruction.

## Git workflow

* Minimize commits and pushes.

* Treat implementation, hardening, tests, audits, and documentation for one feature as a single logical package when practical.

* Commit and push only after the package is stable and only when explicitly requested.

* Never use force push unless explicitly requested.

* Before committing, review the complete diff and ensure no unrelated changes are included.

## Communication

* Keep reports and prompts concise and focused on the current step.

* Do not repeat established project history or rules unnecessarily.

* State what changed, what was validated, and any real remaining issues.

* Do not expand the task beyond its requested scope.

## Release artifacts

* From v1.9.0 onward, follow [the release process](Documentation/RELEASE_PROCESS.md). Store every release outside Git in `C:\Programowanie\RELEASES\WurmMiningCounter\<version>`, using only the numeric version as the directory name.

* Keep Compressed/, Full/, Lite/, ReleaseNotes.md, SHA256SUMS.txt, and the three versioned ZIPs in that directory. Each variant directory must exactly match its ZIP and contain the EXE, LICENSE, README.md, and an empty Sounds/ directory.

* Build each EXE from the release commit with the correct version; verify the unpacked files, ZIP contents, and SHA-256 hashes. Publish the three ZIPs and SHA256SUMS.txt from that directory to GitHub Release, preserve the local copies and all prior version directories, and never add artifacts to Git.

* Include release-standard documentation and version updates in the release preparation commit.
