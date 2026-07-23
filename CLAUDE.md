# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

CtrlAgent turns an Xbox controller (especially the Elite Series 2) into a two-way control surface for AI coding agents: controller inputs become agent commands (submit, interrupt, approve, decline, switch session), and agent lifecycle events come back as distinct rumble patterns. Windows-first, pre-alpha MVP.

## Build and test

The SDK is pinned to .NET 10 `10.0.302` via `global.json` (rolls forward within .NET 10). All managed projects share `Directory.Build.props`: `net10.0`, C# 14, nullable enabled.

```powershell
dotnet restore CtrlAgent.sln
dotnet build CtrlAgent.sln --configuration Release
dotnet run --project tests/CtrlAgent.Tests/CtrlAgent.Tests.csproj --configuration Release
```

Tests are a deliberately dependency-free console harness (no xUnit/NUnit): `tests/CtrlAgent.Tests/Program.cs` holds a `(Name, Func<Task>)` array, prints `PASS`/`FAIL` per test, and exits nonzero on any failure. There is no filter mechanism — to run a single test, temporarily trim the array; to add a test, append a tuple and a local static function.

The native GameInput bridge is a separate C++ project (not in the .sln) and needs MSBuild from a Visual Studio developer shell:

```powershell
msbuild native/CtrlAgent.GameInputBridge/CtrlAgent.GameInputBridge.vcxproj /restore /m /p:Configuration=Release /p:Platform=x64
```

It pins `Microsoft.GameInput` 3.4.x (`GameInputPackageVersion` in the .vcxproj) and compiles against the package-selected `GAMEINPUT_API_VERSION`. CI (`.github/workflows/ci.yml`) runs both halves on `windows-latest` and uploads build logs as artifacts.

Run the app end-to-end without real agent risk: `dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- --agent mock` (or `--agent codex --cwd <repo>`). `src/CtrlAgent.Demo` plays the haptic patterns standalone. `--validate` runs the guided hardware wizard (`ValidationWizard` in the App; report model/gates in Core's `Validation.cs`) and writes `validation/<date>-elite-series-2-<transport>.md`.

## Architecture

Two directions flow through the same pipeline (see `docs/architecture.md`):

- **Control path:** controller adapter → normalized `ControllerInputEvent` → `MappingEngine` → `AgentCommand` → `IAgentAdapter.ExecuteAsync`.
- **Feedback path:** `IAgentAdapter.ReadEventsAsync` → normalized `AgentEvent` → `FeedbackRouter` → `HapticPattern` → `HapticScheduler` → `IControllerDevice.PlayAsync`.

Project boundaries:

- `src/CtrlAgent.Core` — all contracts (`IControllerDevice`, `IAgentAdapter`, events, commands), `MappingEngine`, profile validation/JSON (`Profiles.cs`), `FeedbackRouter`, `HapticPatternCatalog`, `HapticScheduler`. **Must stay platform-independent: no references to GameInput, XInput, Codex, UI frameworks, or keyboard injection** (System.Text.Json is fine — it's BCL).
- `src/CtrlAgent.Platform.Windows` — `WindowsControllerProvider` tries the native GameInput bridge first, then falls back to XInput (P/Invoke in `XInputNative.cs`). Bridge path resolution order: `--gameinput-bridge` argument → `CTRL_AGENT_GAMEINPUT_BRIDGE` env var → `CtrlAgent.GameInputBridge.exe` beside the app.
- `native/CtrlAgent.GameInputBridge` — C++ console exe the managed host spawns; talks newline-delimited JSON over stdio (emits `ready` and input events; accepts `{"type":"rumble", ...}`). This is what exposes the four Elite paddles and trigger rumble — XInput cannot.
- `src/CtrlAgent.Adapters.Mock` / `src/CtrlAgent.Adapters.Codex` / `src/CtrlAgent.Adapters.ClaudeCode` — `IAgentAdapter` implementations. The Codex adapter spawns `codex app-server` and speaks JSON-RPC-style JSONL over stdio, normalizing thread/turn/approval notifications into `AgentEvent`s. On app-server crash it fails in-flight requests, publishes an Error event, and restarts with capped backoff (max 5 attempts); while down, `ExecuteAsync` publishes an Error event instead of throwing. The Claude Code adapter spawns `claude --print --input-format stream-json --output-format stream-json --verbose --permission-prompt-tool stdio`; line classification lives in the pure `ClaudeStreamParser` (unit-tested), `can_use_tool` control requests become ApprovalRequired events answered with allow/deny control responses (allow echoes the tool input as `updatedInput`), NewSession restarts the process, and crash restart mirrors the Codex adapter. The wire shapes follow the Agent SDK protocol and still need verification against a real CLI.
- `src/CtrlAgent.Hosting` — the shared `HostEngine` both hosts build on (references Core only; hosts inject `IControllerProvider`/`IAgentAdapter` and the engine owns their disposal). It runs the controller-session loop (re-acquires a device after disconnect/bridge death; agent commands go through a catch-all so a failing adapter never kills a loop) and the agent-event loop (pending-approval tracking, haptic routing), and raises events on background threads: `LogEmitted`, `ControllerConnected`/`ControllerStatusChanged`, `AgentEventReceived`, `PendingApprovalChanged`. Behavior changes to host loops go here, once.
- `src/CtrlAgent.App` — console host: parses options, builds provider/adapter/profile, wires `HostEngine` events to the console, and runs the interactive command loop. Also owns the `--validate` wizard path.
- `src/CtrlAgent.Gui` — Avalonia desktop app (Fluent dark theme, compiled bindings) on the same `HostEngine`; `MainViewModel` marshals engine events onto the UI thread via `Dispatcher.UIThread.Post`. Tray app: closing the window hides it (`ShutdownMode.OnExplicitShutdown`); tray menu shows/exits. `ProfileEditorWindow` edits bindings with live `ControllerProfileValidator` feedback and applies via `HostEngine.TryApplyProfile` (validated runtime swap that carries pending-approval state over). Avalonia is pinned to the last 11.x (11.3.18).

### Mapping and approval safety

These invariants are tested and must hold:

- `MappingEngine.Process` picks structural matches, keeps only bindings with the **highest modifier count** (chords beat plain buttons), and only then filters by eligibility. Ordering matters: an ineligible approval chord (e.g. RB+A with no pending approval) must produce *no* command rather than falling through to the plain-button action.
- Bindings with `RequiresPendingApproval: true` fire only while an approval request is pending (`SetPendingApproval`, driven by the app's agent loop). Approval commands are hydrated with the pending session/request ids.
- Default profile: paddles map to approve-once / approve-for-session / decline / cancel; RB+A/Y/X/B are the XInput fallback chords. No default mapping may approve a destructive action with a single accidental face-button press.
- Gestures (`InputGesture`: Press, Release, AxisThreshold, Tap, Hold, DoublePress) resolve **from event timestamps**, never wall-clock reads — tap/hold split on release duration, double-press on press-to-press interval — so tests can drive them with explicit `DateTimeOffset`s. `MappingEngine`'s constructor runs `ControllerProfileValidator` and throws on any error; validation rejects duplicate/ambiguous chords (Press mixed with Tap/Hold/DoublePress, Tap with DoublePress) and enforces approval safety (approve/decline need `requiresPendingApproval`; approvals must sit on a paddle, chord, or hold).
- Profiles persist as versioned JSON via `ControllerProfileJson` (`--profile` / `--export-profile` in the app); deserialization validates and throws `FormatException` listing every problem.

### Haptics

`HapticScheduler` serializes playback per controller: `PlayAsync` cancels and drains the previous cue, then returns immediately after scheduling the new one (looping cues like `ApprovalRequired` must not block the agent event loop). Patterns are frame lists (`RumbleFrame`: low/high motor + left/right trigger, clamped 0..1). `AgentStateKind.Idle` stops haptics rather than routing a pattern. The app talks to haptics through `HapticSchedulerHub` (Core), which routes to the scheduler of the currently attached controller — detached = no-op, device-loss exceptions swallowed — so the agent loop survives controller swaps. Device implementations must zero rumble in a `finally` when playback ends or the device is disposed.

## Docs to keep in sync

`docs/architecture.md` (as-built + decision log), `docs/profiles.md` (mapping/gesture reference), `docs/adapters.md` (adapter protocols + authoring guide), `docs/roadmap.md` (backlog with priorities), `CONTRIBUTING.md` (invariants). When changing behavior in those areas, update the matching doc in the same commit.

## Caveats

- The XInput interop, bridge process, and Codex process management are Windows-specific at runtime; the managed solution compiles on any OS, so CI-on-Windows is the real verification.
- Haptic pattern values and the native bridge are still awaiting real Elite-controller validation (`docs/controller-validation.md`); don't treat the catalog values as final.
