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

What must be covered by tests: mapping/gesture semantics, profile validation rules, haptic scheduling behavior, protocol parsers (see `ClaudeStreamParser` and `CodexProtocolParser` for the pattern), presentation rules in `CtrlAgent.Presentation`, and validation-report gates. Adapters keep protocol parsing in pure classes precisely so it stays testable without processes.

**Pure logic goes where a test can reach it.** Two projects exist for that reason alone: adapters put wire classification in a parser class so no process is needed, and `CtrlAgent.Presentation` holds the GUI's decision-making (transcript folding, log severity, approval highlighting) because `CtrlAgent.Gui` targets `net10.0-windows` and a `net10.0` test project cannot reference it. If you find yourself writing a non-trivial rule inside a view model or a process manager, it belongs one layer down.

Warnings are errors (`Directory.Build.props`). The tree is warning-free; keep it that way rather than adding suppressions.

## Invariants

Beyond the mapping and approval-safety rules in `docs/profiles.md`:

- **An interrupt means the same thing on every adapter.** Report it with `AgentInterrupt.State` / `AgentInterrupt.Message`, never with a state an adapter picked for itself. This was wrong once — Claude Code said `Completed`, Codex and the mock said `Idle`, and `Idle` routes to *no* haptic pattern, so the same button gave a confirming buzz on one agent and silence on another. The constant exists so the question cannot be answered twice. Interrupting is the moment you are least likely to be looking at the screen, so it must always be felt.
- **`AgentStateKind.Idle` means "no cue".** `FeedbackRouter` deliberately returns null for it. Do not route an outcome the user needs to feel through `Idle`.
- **Haptic values in `HapticPatternCatalog` are unverified estimates.** Change them only with a real device in hand, and say which device in the commit.

## Seeing the GUI without Windows

`CtrlAgent.Gui` targets `net10.0-windows`, so it cannot run on a Linux dev box or a build agent. `tools/CtrlAgent.UiRender` compiles the *same* XAML and view models against a cross-platform TFM and renders the windows to PNG using Avalonia's headless Skia platform:

```bash
dotnet run --project tools/CtrlAgent.UiRender/CtrlAgent.UiRender.csproj --configuration Release
# PNGs land in ./shots (override with UIRENDER_OUT)
```

It renders the main window (idle, approval, conversation, first-run, startup-error, and a narrow window at the declared minimum size), Mainframe in five states, the profile editor, overlay, toast, and workspace picker. **Run it and look at the PNGs after any XAML change** — it catches clipped controls, empty panels, and cards drawn over each other, none of which the compiler or the unit tests can see.

It also fails rather than just reporting: every visible `Border.card` must have non-zero bounds inside the window **and a non-zero effective opacity**, a surface that renders no frame is a fault, and any fault exits nonzero. The opacity check exists because an entry animation whose delay outlives the capture leaves every card correctly laid out and completely invisible — geometry alone passes that happily and the screenshot shows only the wallpaper.

Buttons have one state model, declared at the end of the button section in `App.axaml` so it wins over the variants: hover lifts the fill, **press darkens it below rest and adds an inset shadow**, disabled is a flat neutral surface rather than a faded coloured one. The harness enforces the press rule by measurement (`CheckPressDarkens`): it presses each variant with real input and requires the fill to be darker than the hover state. That check exists because `approve` and `deny` had drifted to rest #26 → hover #4D → pressed #66 — the harder you pushed, the brighter they got. It is measured rather than screenshotted because `:pressed` is driven by the read-only `Button.IsPressed` and cannot be forced, and real input can only press one button at a time. `17-buttons.png` renders the rest/hover/disabled matrix with pseudo-classes forced.

**Anything with `Animation.Delay` must also set its pre-animation value in markup** (`Opacity="0"`, `RenderTransform="scaleX(0)"`). During the delay Avalonia renders the element's own value, so a delayed fade-in shows the element at full opacity until its turn — which is exactly backwards. The `ui-render` CI job runs it on every push and uploads the screenshots as artifacts.

**The animation clock does advance** — `Pump` sleeps in real time between render ticks, and Avalonia's headless clock follows real time — so these are genuine frames of running animations. The harness used to claim otherwise and every intro shot was taken on that false assumption. `RenderAt` uses it to sample the Mainframe boot sequence at five points (`20-`…`24-boot-*.png`); without those, the intro is the one part of the app nobody can review, because it plays for three seconds on a machine none of us is sitting at.

One real caveat remains: Windows chrome (caption buttons) is absent, so title-bar overlap still needs a Windows check. The tool is deliberately outside `CtrlAgent.sln`, which is exactly why the CI job matters — a normal build never compiles it.

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
