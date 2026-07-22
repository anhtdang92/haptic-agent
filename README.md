<p align="center">
  <img src="assets/logo.png" alt="CtrlAgent" width="640">
</p>

# CtrlAgent

**CtrlAgent** is a Windows-first, open-source controller interface for AI coding agents.

It turns an Xbox controller—especially the Xbox Elite controller—into a two-way agent control surface:

- Controller inputs trigger actions such as submit, interrupt, review, approve, decline, and switch session.
- Agent lifecycle events return to the controller as distinct haptic patterns.
- A local mapping engine supports normal buttons, modifier chords, and independent Elite paddles when the GameInput bridge is available.

> **Status:** pre-alpha MVP. The managed application, mappings, mock-agent loop, Codex app-server adapter, XInput fallback, tests, and CI are implemented. The native GameInput bridge still requires real Elite-controller validation.

## Data flow

```text
Xbox controller
      |
      v
GameInput bridge or XInput -> Mapping engine -> Agent adapter
      ^                            |                 |
      |                            v                 v
Haptic scheduler <--------- Feedback router <- Agent events
```

## Implemented

- .NET 10 managed host and platform-independent core
- XInput controller discovery, buttons, sticks, triggers, reconnect handling, and two-motor rumble
- Native GameInput v3 bridge for four Elite paddles and four-channel rumble
- XInput fallback approval chords when independent paddles are unavailable
- Safe mapping priority that prevents approval chords from falling through to ordinary actions
- Versioned JSON controller profiles with press, release, tap, hold, double-press, and axis-threshold gestures, collision detection, and validated approval safeguards
- Crash resilience: Codex app-server restart with backoff, controller reconnect without restarting the host, and rumble that always stops when a device or cue goes away
- Avalonia desktop GUI with live status, approval actions, prompt submission, haptic preview, and event log
- Cancellable haptic scheduler and distinct working, approval, waiting, completion, and error patterns
- Mock agent adapter for end-to-end testing without Codex
- Codex app-server JSONL adapter with thread creation, turn submission, interruption, lifecycle events, and approval responses
- Dependency-free automated test harness
- Windows GitHub Actions builds for managed and native components

## Default controller mappings

| Input | Action |
|---|---|
| A | Submit the configured default prompt |
| B | Interrupt the active turn |
| X | Ask the agent to review changes |
| Menu | Create a new session |
| D-pad left/right | Previous/next session |
| LB + A | Run tests and fix failures |
| Elite paddles | Approve once / approve for session / decline / cancel |
| RB + A/Y/X/B | XInput fallback for the four approval actions |

Approval inputs do nothing unless an approval request is actually pending.

## Custom profiles

Mappings are configurable through versioned JSON profiles:

```powershell
dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- --export-profile my-profile.json
dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- --agent mock --profile my-profile.json
```

Each binding names a control, a gesture, and a command:

```json
{
  "version": 1,
  "name": "my-profile",
  "bindings": [
    { "control": "a", "gesture": "press", "command": "submitPrompt" },
    { "control": "x", "gesture": "doublePress", "command": "reviewChanges" },
    {
      "control": "paddleLeft1",
      "gesture": "hold",
      "holdMilliseconds": 400,
      "command": "approveOnce",
      "requiresPendingApproval": true
    }
  ]
}
```

Supported gestures: `press`, `release`, `tap`, `hold`, `doublePress`, and `axisThreshold` (with `minimumValue`). `modifiers` turns a binding into a chord, and `text` overrides the prompt for `submitPrompt`.

Profiles are validated before they load. Ambiguous combinations (for example `press` and `hold` on the same chord) are rejected, `approveOnce`/`approveForSession`/`decline` bindings must set `requiresPendingApproval`, and approvals must sit on a paddle, a chord, or a hold gesture so a single accidental face-button press can never approve anything.

## Requirements

- Windows 10 19H1 or newer
- .NET 10 SDK `10.0.302` or a compatible later .NET 10 SDK
- Visual Studio C++ build tools for the optional native GameInput bridge
- Microsoft GameInput redistributable for GameInput v3 functionality
- Codex installed and authenticated when using `--agent codex`

## Build and test

```powershell
dotnet restore CtrlAgent.sln
dotnet build CtrlAgent.sln --configuration Release
dotnet run --project tests/CtrlAgent.Tests/CtrlAgent.Tests.csproj --configuration Release
```

Build the native bridge from a Visual Studio developer shell:

```powershell
msbuild native/CtrlAgent.GameInputBridge/CtrlAgent.GameInputBridge.vcxproj /restore /m /p:Configuration=Release /p:Platform=x64
```

## Run the desktop GUI

An Avalonia desktop app provides live controller/agent status, an approval banner with the four approval actions, prompt submission, a haptic-pattern preview, the active profile's bindings, and an event log:

```powershell
dotnet run --project src/CtrlAgent.Gui/CtrlAgent.Gui.csproj -- --agent mock
```

The GUI accepts the same `--agent`, `--cwd`, `--prompt`, `--codex-path`, `--gameinput-bridge`, and `--profile` options as the console host.

## Run with the mock agent

Connect an Xbox controller and run:

```powershell
dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- --agent mock
```

The mock agent can produce working, approval-required, completed, and interrupted states so the full controller-to-agent-to-rumble loop can be tested safely.

## Run with Codex

```powershell
dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- \
  --agent codex \
  --cwd C:\path\to\your\repository
```

Useful options:

```text
--prompt TEXT             Prompt sent by A
--codex-path PATH         Explicit Codex executable path
--gameinput-bridge PATH   Explicit native bridge executable path
--profile PATH            Load a controller profile from a JSON file
--export-profile PATH     Write the default profile as JSON and exit
--verbose                 Print analog controller changes
```

When the native bridge is not supplied or cannot start, CtrlAgent automatically falls back to XInput. Place `CtrlAgent.GameInputBridge.exe` beside the managed application, set `CTRL_AGENT_GAMEINPUT_BRIDGE`, or pass `--gameinput-bridge` to enable independent paddles and trigger rumble.

## Repository layout

```text
assets/
  logo.png                     CtrlAgent logo

native/
  CtrlAgent.GameInputBridge/   GameInput v3 Elite-paddle and rumble bridge

src/
  CtrlAgent.Core/              Contracts, mappings, feedback, and haptics
  CtrlAgent.Platform.Windows/  GameInput bridge client and XInput fallback
  CtrlAgent.Adapters.Mock/     Safe simulated agent
  CtrlAgent.Adapters.Codex/    Codex app-server adapter
  CtrlAgent.App/               End-to-end Windows console host
  CtrlAgent.Gui/               Avalonia desktop app (status, approvals, log)
  CtrlAgent.Demo/              Haptic-pattern demonstration

tests/
  CtrlAgent.Tests/             Dependency-free automated tests

docs/
  architecture.md
  controller-validation.md
  roadmap.md
```

## Hardware validation still required

Software builds cannot prove how every Elite firmware and connection mode behaves. The first real-device pass must verify:

- all four paddles over USB, Xbox Wireless, and Bluetooth;
- low/high and trigger-rumble channels;
- focus/background behavior;
- disconnect and reconnect behavior;
- paddle behavior with Xbox Accessories profiles.

See [the controller validation plan](docs/controller-validation.md).

## License

CtrlAgent is licensed under the [MIT License](LICENSE).

## Trademark notice

Xbox and Xbox Elite are trademarks of Microsoft. Codex is a trademark of OpenAI. CtrlAgent is an independent open-source project and is not affiliated with or endorsed by Microsoft or OpenAI.
