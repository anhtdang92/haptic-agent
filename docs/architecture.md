# HapticAgent Architecture

Status: **Accepted for the first hardware spike**

This document defines the smallest architecture that lets us validate Xbox Elite input and two-way haptic feedback without prematurely choosing a polished desktop UI.

## Product loop

```text
Controller input
      |
      v
Controller adapter -> Mapping engine -> Agent adapter
      ^                    |                 |
      |                    v                 v
Haptic scheduler <- Feedback router <- Agent events
```

HapticAgent has two directions:

1. **Control path:** physical input becomes a normalized control event, a mapping resolves that event into an agent command, and an agent adapter executes it.
2. **Feedback path:** an agent adapter emits normalized lifecycle events, the feedback router selects a haptic cue, and the controller adapter plays it.

## Architectural boundaries

### HapticAgent.Core

A platform-independent .NET library containing:

- normalized controller controls and input events;
- agent commands and lifecycle events;
- haptic patterns and frames;
- interfaces for controller and agent adapters;
- mapping and feedback-routing logic.

The core project must not reference GameInput, WinUI, Codex, Claude Code, or keyboard-injection APIs.

### HapticAgent.GameInput

A Windows-specific adapter responsible for:

- discovering supported controllers;
- reading buttons, axes, triggers, and paddles;
- reporting device capabilities and connection information;
- playing low-frequency, high-frequency, and trigger-rumble output;
- handling disconnects, reconnects, and application focus.

Microsoft GameInput is a native API. The initial hardware validation should therefore be a small native C++ console spike. After the API behavior is confirmed, we will expose only the narrow functions needed by the managed host through a stable C ABI or an equivalent generated interop layer.

### HapticAgent.AgentProtocol

Shared contracts for agent adapters. The first implementation will target Codex app-server over its supported local stdio transport. It will normalize Codex-specific notifications and server-initiated approval requests into HapticAgent events.

### HapticAgent.App

The eventual Windows tray application. It will own configuration, profile selection, device status, the mapping editor, and a compact HUD. We will select the UI framework only after the hardware spike.

## Technology decisions

### Managed host: .NET 10 LTS

The main application and core logic target .NET 10. The managed host is a good fit for configuration, JSON-RPC, process management, asynchronous event pipelines, testing, and Windows desktop UI.

### Hardware API: Microsoft GameInput 3.4

GameInput 3.3 added controller-paddle support, and 3.4 added raw HID reports. The first spike pins the current 3.4 package line and explicitly validates Xbox Elite Series 2 behavior rather than assuming every transport exposes identical data.

### Codex transport: local stdio first

Codex app-server supports a bidirectional JSON-RPC-style protocol. The stable first integration will start and own an app-server process using newline-delimited messages over stdio. Experimental WebSocket transport is not part of the initial design.

### Event-driven core

Adapters expose asynchronous event streams. The core does not poll agent state or controller state itself. Each adapter owns its platform-specific input pump and publishes normalized events to the host.

## Core contracts

```text
IControllerDevice
  ReadEventsAsync()
  PlayAsync(pattern)

IAgentAdapter
  ReadEventsAsync()
  ExecuteAsync(command)

FeedbackRouter
  AgentEvent -> HapticPattern?

MappingEngine
  ControllerInputEvent + active profile -> AgentCommand?
```

## Threading model

- Controller input is produced by the GameInput adapter and written to a bounded channel.
- Agent events are produced by each adapter and written to a separate bounded channel.
- Mapping and feedback routing run in managed background tasks.
- Haptic playback is serialized per controller so patterns cannot race each other.
- Higher-priority cues such as approval and error may interrupt lower-priority cues such as working or session-switch confirmation.
- Every long-running operation accepts a `CancellationToken` and must shut down cleanly.

## Safety rules

- No default mapping may approve a destructive action with a single accidental face-button press.
- Approval mappings should use a rear paddle, hold, or chord and must be clearly visible in the active profile.
- Session-wide approval is a separate command from one-time approval.
- Keyboard and mouse injection are fallback adapters, not the primary integration path.
- HapticAgent does not attempt to bypass anti-cheat, access-control, sandbox, or agent-approval systems.
- Agent adapters must surface the exact session and request associated with an approval before accepting a controller response.

## Configuration direction

Profiles will eventually be stored as versioned JSON. A profile contains:

- device match criteria;
- layers and modifier controls;
- press, release, hold, double-press, chord, and axis-threshold bindings;
- command parameters;
- haptic cue overrides;
- safeguards for approval-capable bindings.

We will not finalize the schema until raw controller events are recorded from real hardware.

## First vertical slice

The first useful vertical slice is deliberately small:

1. A native GameInput spike detects an Xbox Elite Series 2 controller.
2. It logs all standard controls and all four paddle flags.
3. It plays four distinct rumble patterns.
4. A managed demo emits mock agent lifecycle events.
5. The core feedback router converts those events into patterns.
6. The native spike and managed demo are joined only after both halves are independently validated.

## Deferred decisions

- WinUI 3 versus WPF versus Avalonia for the desktop application.
- C ABI bridge versus direct source-generated COM/P/Invoke interop.
- Whether a long-running local service is needed or a tray process is sufficient.
- Multi-controller arbitration.
- Remote or mobile control surfaces.
- Third-party agent adapter packaging.

## Primary references

- Microsoft GameInput overview: https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-overview
- Microsoft GameInput gamepad buttons and Elite paddles: https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/enums/gameinputgamepadbuttons
- Microsoft GameInput rumble: https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/interfaces/igameinputdevice/methods/igameinputdevice_setrumblestate
- Microsoft.GameInput NuGet package: https://www.nuget.org/packages/Microsoft.GameInput
- Codex app-server protocol: https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md
