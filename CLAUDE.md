# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

HapticAgent turns an Xbox controller (especially the Elite Series 2) into a two-way control surface for AI coding agents: controller inputs become agent commands (submit, interrupt, approve, decline, switch session), and agent lifecycle events come back as distinct rumble patterns. Windows-first, pre-alpha MVP.

## Build and test

The SDK is pinned to .NET 10 `10.0.302` via `global.json` (rolls forward within .NET 10). All managed projects share `Directory.Build.props`: `net10.0`, C# 14, nullable enabled.

```powershell
dotnet restore HapticAgent.sln
dotnet build HapticAgent.sln --configuration Release
dotnet run --project tests/HapticAgent.Tests/HapticAgent.Tests.csproj --configuration Release
```

Tests are a deliberately dependency-free console harness (no xUnit/NUnit): `tests/HapticAgent.Tests/Program.cs` holds a `(Name, Func<Task>)` array, prints `PASS`/`FAIL` per test, and exits nonzero on any failure. There is no filter mechanism — to run a single test, temporarily trim the array; to add a test, append a tuple and a local static function.

The native GameInput bridge is a separate C++ project (not in the .sln) and needs MSBuild from a Visual Studio developer shell:

```powershell
msbuild native/HapticAgent.GameInputBridge/HapticAgent.GameInputBridge.vcxproj /restore /m /p:Configuration=Release /p:Platform=x64
```

It pins `Microsoft.GameInput` 3.4.x (`GameInputPackageVersion` in the .vcxproj) and compiles against the package-selected `GAMEINPUT_API_VERSION`. CI (`.github/workflows/ci.yml`) runs both halves on `windows-latest` and uploads build logs as artifacts.

Run the app end-to-end without real agent risk: `dotnet run --project src/HapticAgent.App/HapticAgent.App.csproj -- --agent mock` (or `--agent codex --cwd <repo>`). `src/HapticAgent.Demo` plays the haptic patterns standalone.

## Architecture

Two directions flow through the same pipeline (see `docs/architecture.md`):

- **Control path:** controller adapter → normalized `ControllerInputEvent` → `MappingEngine` → `AgentCommand` → `IAgentAdapter.ExecuteAsync`.
- **Feedback path:** `IAgentAdapter.ReadEventsAsync` → normalized `AgentEvent` → `FeedbackRouter` → `HapticPattern` → `HapticScheduler` → `IControllerDevice.PlayAsync`.

Project boundaries:

- `src/HapticAgent.Core` — all contracts (`IControllerDevice`, `IAgentAdapter`, events, commands), `MappingEngine`, `FeedbackRouter`, `HapticPatternCatalog`, `HapticScheduler`. **Must stay platform-independent: no references to GameInput, XInput, Codex, UI frameworks, or keyboard injection.**
- `src/HapticAgent.Platform.Windows` — `WindowsControllerProvider` tries the native GameInput bridge first, then falls back to XInput (P/Invoke in `XInputNative.cs`). Bridge path resolution order: `--gameinput-bridge` argument → `HAPTIC_AGENT_GAMEINPUT_BRIDGE` env var → `HapticAgent.GameInputBridge.exe` beside the app.
- `native/HapticAgent.GameInputBridge` — C++ console exe the managed host spawns; talks newline-delimited JSON over stdio (emits `ready` and input events; accepts `{"type":"rumble", ...}`). This is what exposes the four Elite paddles and trigger rumble — XInput cannot.
- `src/HapticAgent.Adapters.Mock` / `src/HapticAgent.Adapters.Codex` — `IAgentAdapter` implementations. The Codex adapter spawns `codex app-server` and speaks JSON-RPC-style JSONL over stdio, normalizing thread/turn/approval notifications into `AgentEvent`s.
- `src/HapticAgent.App` — console host wiring everything together: three concurrent loops (controller input, agent events, console commands) sharing `MappingEngine` and a `HostState` that tracks the pending approval request.

### Mapping and approval safety

These invariants are tested and must hold:

- `MappingEngine.Process` picks structural matches, keeps only bindings with the **highest modifier count** (chords beat plain buttons), and only then filters by eligibility. Ordering matters: an ineligible approval chord (e.g. RB+A with no pending approval) must produce *no* command rather than falling through to the plain-button action.
- Bindings with `RequiresPendingApproval: true` fire only while an approval request is pending (`SetPendingApproval`, driven by the app's agent loop). Approval commands are hydrated with the pending session/request ids.
- Default profile: paddles map to approve-once / approve-for-session / decline / cancel; RB+A/Y/X/B are the XInput fallback chords. No default mapping may approve a destructive action with a single accidental face-button press.

### Haptics

`HapticScheduler` serializes playback per controller: `PlayAsync` cancels and drains the previous cue, then returns immediately after scheduling the new one (looping cues like `ApprovalRequired` must not block the agent event loop). Patterns are frame lists (`RumbleFrame`: low/high motor + left/right trigger, clamped 0..1). `AgentStateKind.Idle` stops haptics rather than routing a pattern.

## Caveats

- The XInput interop, bridge process, and Codex process management are Windows-specific at runtime; the managed solution compiles on any OS, so CI-on-Windows is the real verification.
- Haptic pattern values and the native bridge are still awaiting real Elite-controller validation (`docs/controller-validation.md`); don't treat the catalog values as final.
