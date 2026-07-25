# Contributing to CtrlAgent

## Prerequisites

- .NET 10 SDK `10.0.302` (pinned via `global.json`, rolls forward within .NET 10)
- Windows 10 19H1+ for running; the managed solution *builds* on any OS
- Visual Studio C++ build tools + MSBuild only for the native GameInput bridge

## Build and test

```powershell
dotnet restore CtrlAgent.sln
dotnet build CtrlAgent.sln --configuration Release
dotnet run --project tests/CtrlAgent.Tests/CtrlAgent.Tests.csproj --configuration Release
```

On Linux/macOS add `-p:EnableWindowsTargeting=true` to build the Windows-targeted projects (App, Gui, Platform.Windows). CI (`.github/workflows/ci.yml`) on `windows-latest` is the authoritative check; it also builds the native bridge:

```powershell
msbuild native/CtrlAgent.GameInputBridge/CtrlAgent.GameInputBridge.vcxproj /restore /m /p:Configuration=Release /p:Platform=x64
```

Run end-to-end without agent risk: `--agent mock` (console host or GUI). `--validate` runs the hardware wizard.

## Tests

The harness is deliberately dependency-free: `tests/CtrlAgent.Tests/Program.cs` holds a `(Name, Func<Task>)` array, prints `PASS`/`FAIL`, and exits nonzero on failure. To add a test, append a tuple and a local static function. There is no filter mechanism — temporarily trim the array to isolate one test.

What must be covered by tests: mapping/gesture semantics, profile validation rules, haptic scheduling behavior, protocol parsers (see `ClaudeStreamParser` tests for the pattern), and validation-report gates. Adapters keep protocol parsing in pure classes precisely so it stays testable without processes.

## Seeing the GUI without Windows

`CtrlAgent.Gui` targets `net10.0-windows`, so it cannot run on a Linux dev box or a build agent. `tools/CtrlAgent.UiRender` compiles the *same* XAML and view models against a cross-platform TFM and renders the windows to PNG using Avalonia's headless Skia platform:

```bash
dotnet run --project tools/CtrlAgent.UiRender --configuration Release
# PNGs land in tools/CtrlAgent.UiRender/shots (override with UIRENDER_OUT)
```

It renders the main window (idle and approval states), Big Picture, and a focus-walked tile rail, and prints layout diagnostics for animation-gated elements. Use it after layout changes — it catches clipped controls, empty panels, and off-screen focus that unit tests cannot. Two caveats: the animation clock does not advance, so elements mid-animation render at their starting opacity, and real Windows chrome (caption buttons) is absent, so title-bar overlap still needs a Windows check. The tool is deliberately outside `CtrlAgent.sln`.

## Invariants — do not break

These are enforced by tests and/or documented contracts; changes touching them need matching test updates:

1. **Core purity.** `CtrlAgent.Core` references BCL only — no GameInput/XInput, no agent CLIs, no UI frameworks.
2. **Approval safety.** Chord specificity resolves before eligibility (an ineligible approval chord yields *no* command, never the plain-button action). Approvals must be paddle/chord/hold and pending-gated — validator-enforced.
3. **Non-blocking haptics.** `HapticScheduler.PlayAsync` returns after scheduling; looping cues must never block the agent event loop. Devices zero rumble in `finally` and on dispose.
4. **Resilient loops.** Controller loss, agent-process death, and command failures produce events/logs and recovery — never a host crash.
5. **Deterministic gestures.** Gesture math uses event timestamps, not wall-clock reads.
6. **Layer activation follows the device.** Profile layers activate on the connected controller's capabilities (`SetDeviceCapabilities` on every connect, carried across runtime profile swaps); with no device known, every layer is active. Collision validation runs per capability world — mutually exclusive layers may share chords, base bindings collide with everything.

## Conventions

- Conventional-commit style subjects (`feat(scope): …`, `fix: …`, `docs: …`, `ci: …`), imperative mood.
- `Directory.Build.props` applies to all managed projects: net10.0, C# 14, nullable enabled. Keep builds warning-free.
- Host-loop behavior (controller sessions, agent events, pending approvals) lives in `CtrlAgent.Hosting.HostEngine` — change it there, not in the App/Gui wiring.
- Update docs with behavior: `docs/profiles.md` for mapping changes, `docs/adapters.md` for adapter/protocol changes, `docs/architecture.md` decision log for design decisions, `CLAUDE.md` for anything a future coding session must know.

## Docs map

| Doc | Contents |
|---|---|
| `docs/architecture.md` | As-built architecture, threading model, decision log |
| `docs/profiles.md` | Profile JSON reference, gestures, validation rules |
| `docs/adapters.md` | Adapter contract, per-adapter protocols, authoring guide |
| `docs/roadmap.md` | Phase ledger, prioritized backlog, release targets |
| `docs/controller-validation.md` | Hardware validation plan + wizard |
| `CLAUDE.md` | Guidance for Claude Code sessions |
