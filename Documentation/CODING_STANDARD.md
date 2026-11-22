# Coding standard

## Workflow

- Prefer one small step and one file per step where practical.
- A larger step is acceptable when adapting a proven, coherent mechanism.
- After a significant step, run a build and the relevant tests.
- Audit before increasing complexity.
- Update documentation alongside architecture or semantic changes.
- Do not expand a step's scope without need; complete and verify the current change first.

## Architecture

- Create small classes with a single responsibility.
- `App` remains the composition root.
- ViewModels do not own monitoring infrastructure, parsers, tray, or Settings.
- Parsers remain stateless unless the domain requires state.
- Cross-thread snapshots must be immutable or deeply copied.
- Do not add NuGet packages or dependencies without a concrete need.
- Adapt proven mechanisms instead of redesigning them without reason.

### Shared mechanisms and reuse

- Do not create a parallel mechanism for behavior that can delegate to an existing domain operation or shared primitive.
- Different UI entry points for the same operation should delegate to the same application/domain layer rather than mutate state independently.
- Preserve semantic differences at the appropriate layer. Sharing a primitive must not erase intentional differences in higher-level effects.
- Before adding a class, service, helper, or command, check whether an existing mechanism can be extended safely.

## Domain facts and design decisions

- Confirm exact literals and Wurm behavior primarily from real Event logs.
- For empirical facts, use this precedence: real logs → fixtures/tests → documentation.
- Intentional program semantics are defined by the current `FROZEN DECISION`; Architecture/Specification describe required behavior, while code and tests confirm its implementation.
- A new real log may confirm or refute an earlier literal assumption, but does not automatically change program semantics. Semantic changes require an explicit decision and documentation update.
- Do not present a hypothesis as `LOG-CONFIRMED`. Mark missing evidence `DATA-NEEDED` and deliberately postponed work `DEFERRED`.
- When active documents conflict, do not guess. Determine the source of truth and resolve the contradiction before further implementation.

## Concurrency and lifecycle

- `IDisposable` implementations have an explicit, idempotent lifecycle.
- After `Dispose()` begins, callbacks must not start new work.
- Do not publish callbacks while holding a state lock if the recipient could trigger a reentrant lifecycle.
- WPF logic invoked from Windows Forms or a background worker must use the Dispatcher.
- Do not suppress domain-input errors without justification.

## Testing

- Concurrency tests use deterministic synchronization and timeouts, not arbitrary long `Sleep` calls.
- File tests use temporary directories and clean them up afterward.
- Process tests isolate mutex, pipe, and settings from the user environment.
- A new UI entry point to an existing operation should have a test confirming delegation to the shared mechanism rather than an independent state mutation.
