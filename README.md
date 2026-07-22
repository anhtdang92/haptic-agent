# HapticAgent

**HapticAgent** is a Windows-first, open-source controller interface for AI coding agents.

It turns an Xbox controller—especially the Xbox Elite controller—into a two-way agent control surface:

- Controller inputs trigger actions such as approve, reject, interrupt, review, and switch session.
- Agent lifecycle events return to the controller as distinct haptic patterns.
- A local mapping engine supports profiles, layers, chords, holds, and per-agent adapters.

> **Status:** pre-alpha architecture and hardware-validation phase. The repository does not control a physical controller yet.

## The idea

```text
Xbox controller
      |
      v
GameInput adapter -> Mapping engine -> Coding-agent adapter
      ^                    |                  |
      |                    v                  v
Haptic scheduler <- Feedback router <- Agent events
```

Unlike a normal controller remapper, HapticAgent is designed as a two-way loop. A controller can send commands to an agent, while approvals, completion, errors, and waiting states can produce recognizable vibration cues.

## First target

- Windows 10 19H1 or newer
- Xbox Elite Wireless Controller Series 2
- USB validation first, then Xbox Wireless and Bluetooth
- Microsoft GameInput 3.4 for controller input and rumble
- Codex app-server as the first real agent integration
- .NET 10 LTS for the managed host and platform-independent core

## Current code

The first code establishes protocol-independent contracts for:

- controller controls, including four Elite paddles;
- controller connection and input events;
- agent lifecycle events and commands;
- four-motor haptic frames and reusable patterns;
- agent and controller adapter interfaces;
- routing normalized agent states to haptic cues.

`HapticAgent.Demo` prints the haptic frames that would be sent for mock agent events. It deliberately does not pretend that controller hardware has already been validated.

## Build the current demo

Install the .NET 10 SDK, then run:

```powershell
dotnet build HapticAgent.sln
dotnet run --project src/HapticAgent.Demo/HapticAgent.Demo.csproj
```

The repository currently pins SDK feature band `10.0.302` and permits later .NET 10 feature bands through `rollForward`.

## Milestone 0: prove the hardware loop

1. Discover a connected Xbox Elite Series 2 controller.
2. Log buttons, sticks, triggers, and all four paddle flags.
3. Validate USB, Xbox Wireless Adapter, and Bluetooth behavior separately.
4. Play distinct low-, high-, and trigger-rumble patterns.
5. Confirm reconnect and focus behavior.
6. Join the validated GameInput adapter to the existing core contracts.

We will not start a polished mapping UI until these assumptions are proven on real hardware.

## Repository layout

```text
docs/
  architecture.md             System blueprint and boundaries
  controller-validation.md    Real-hardware test matrix and go/no-go gates

src/
  HapticAgent.Core/            Platform-independent contracts and routing
  HapticAgent.Demo/            Mock agent-to-haptic console demonstration
```

## Design principles

- **Direct integrations first:** use agent APIs and lifecycle events where available; keyboard injection is a fallback.
- **Local-first:** mappings and event routing remain on the user's PC by default.
- **Safe approvals:** destructive or session-wide approvals require deliberate bindings.
- **Adapter-based:** GameInput, Codex, and future integrations stay outside the core.
- **Honest capability reporting:** transport and device differences are tested and surfaced rather than hidden.

## Documentation

- [Architecture blueprint](docs/architecture.md)
- [Xbox Elite validation plan](docs/controller-validation.md)

## License

HapticAgent is licensed under the [MIT License](LICENSE).

## Trademark notice

Xbox and Xbox Elite are trademarks of Microsoft. Codex is a trademark of OpenAI. HapticAgent is an independent open-source project and is not affiliated with or endorsed by Microsoft or OpenAI.
