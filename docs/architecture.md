# CtrlAgent Architecture

Status: **As-built** (updated 2026-07-23). This describes the implemented system; open questions live in the decision log at the bottom. The original pre-implementation blueprint is in this file's git history.

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

CtrlAgent has two directions through one pipeline:

1. **Control path:** a controller adapter emits normalized `ControllerInputEvent`s; `MappingEngine` resolves them against the active profile into `AgentCommand`s; an `IAgentAdapter` executes them.
2. **Feedback path:** the agent adapter emits normalized `AgentEvent`s; `FeedbackRouter` selects a `HapticPattern`; `HapticScheduler` plays it on the controller.

## Component map

```text
                    +----------------------+   +----------------------+
                    |   CtrlAgent.App      |   |   CtrlAgent.Gui      |
                    |   (console host)     |   |   (Avalonia desktop) |
                    +----------+-----------+   +-----------+----------+
                               |     CtrlAgent.Hosting     |
                               |  (shared HostEngine: the  |
                               |   loops live here once)   |
            +------------------+---------------------------+-----------------+
            |                              |                                 |
+-----------v-----------+     +------------v-------------+     +-------------v-----------+
| CtrlAgent.Platform.   |     |     CtrlAgent.Core       |     | CtrlAgent.Adapters.*    |
| Windows               |     | contracts, MappingEngine,|     | Mock / Codex /          |
| GameInput bridge      |     | profiles + validation,   |     | ClaudeCode              |
| client, DualSense HID |     | FeedbackRouter, haptics, |     | (stdio JSONL processes) |
| device, XInput        |     | validation reports       |     |                         |
| fallback              |     +--------------------------+     +-------------------------+
+-----+-----------+-----+
      |           |
      |     +-----v----------------------+
      |     | CtrlAgent.Controllers.     |
      |     | DualSense (pure wire       |
      |     | protocol, no OS calls)     |
      |     +----------------------------+
+-----v----------------------+
| native/CtrlAgent.          |
| GameInputBridge (C++ exe,  |
| GameInput v3, spawned via  |
| stdio JSONL)               |
+----------------------------+
```

Dependency rules:

- **CtrlAgent.Core** is platform-independent and referenced by everything. It must not reference GameInput, XInput, Codex, Claude Code, UI frameworks, or keyboard injection. BCL-only (System.Text.Json is fine).
- **CtrlAgent.Controllers.DualSense** is the pure DualSense wire protocol (input-report parsing, output-report building, Bluetooth CRC) with no OS calls, so it is fully unit-testable; `Platform.Windows` wraps it with SetupAPI/hid.dll I/O.
- **CtrlAgent.Hosting** references Core only: `HostEngine` takes `IControllerProvider` + `IAgentAdapter` from the host, owns their disposal, runs the controller-session and agent-event loops, and raises events (log, controller status, agent events, pending approval). It is fully testable with fake providers/adapters.
- Hosts (App, Gui) are the only projects that reference platform and adapters together; they construct concrete providers/adapters and hand them to the engine.
- Adapters reference Core only. Platform.Windows references Core only.

## Controller side

`WindowsControllerProvider` resolves the primary controller:

1. **GameInput bridge** (preferred): spawns the native C++ exe and speaks newline-delimited JSON over stdio. Path resolution: `--gameinput-bridge` argument → `CTRL_AGENT_GAMEINPUT_BRIDGE` env var → `CtrlAgent.GameInputBridge.exe` beside the app. The bridge is the trigger-rumble path and the *intended* paddle path — but hardware validation (2026-07-24) showed the PC GameInput redistributable never reports Elite Series 2 paddles (unmapped paddles are silent; mapped ones arrive as face buttons) and does not enumerate Bluetooth Xbox controllers at all, so the bridge reports `hasFourPaddles: false` (activating the `withoutPaddles` chord layer) and paddle support is experimental pending a raw-report path. GameInput also gates input on window focus. See `controller-validation.md` for the evidence.
2. **DualSense over raw HID**: enumerates Sony VID `0x054C` (DualSense `0x0CE6`, DualSense Edge `0x0DF2`) via SetupAPI and reads/writes HID reports directly — USB report `0x01`, Bluetooth report `0x31` (CRC32-protected). Buttons map positionally (Cross→A, Circle→B, Square→X, Triangle→Y); Edge rear paddles and Fn map to the four paddle controls. Byte layout is community-documented and still needs real-pad verification.
3. **XInput fallback**: P/Invoke polling (8 ms connected, 250 ms disconnected). No paddles, two motors only; approval actions fall back to RB chords.

Bridge wire protocol (one JSON object per line):

- bridge → host: `{"type":"ready", ...capabilities}`, `{"type":"connected"}`, `{"type":"disconnected"}`, `{"type":"button","control":"A","pressed":true}`, `{"type":"axis","control":"LeftTrigger","value":0.42}`
- host → bridge: `{"type":"rumble","low":0..1,"high":0..1,"leftTrigger":0..1,"rightTrigger":0..1}`, `{"type":"stop"}`

A bridge whose process died reports `IsDefunct`; the provider disposes it, allows one fresh bridge attempt, then falls back to XInput.

## Mapping and profiles

`MappingEngine.Process` is a pure state machine over the input event stream:

1. **Layer filter** — bindings in inactive layers are dropped first. Layers activate on device capability (`always` / `requiresPaddles` / `withoutPaddles`); the host calls `SetDeviceCapabilities` on every controller connect, and with no device known every layer is active.
2. **Structural match** — control, gesture, and (for chords) currently-held modifiers. Gestures: `Press`, `Release`, `AxisThreshold`, `Tap`, `Hold`, `DoublePress`. Tap/hold split on press-to-release duration; double-press on press-to-press interval — both measured **from event timestamps**, never wall-clock reads, so resolution is deterministic and clock-free in tests.
3. **Specificity** — only bindings with the highest modifier count survive (chords beat plain buttons).
4. **Eligibility** — `RequiresPendingApproval` bindings fire only while an approval request is pending. Eligibility runs *after* specificity so an ineligible chord produces no command rather than falling through to the plain-button action.

Profiles are versioned JSON (`ControllerProfileJson`, version 1) validated by `ControllerProfileValidator` at engine construction and on every load — an unsafe or ambiguous profile can never run. See [profiles.md](profiles.md) for the full reference and safety rules.

## Agent side

`IAgentAdapter` exposes `StartAsync`, `ReadEventsAsync` (an async stream of `AgentEvent`s), and `ExecuteAsync(AgentCommand)`. Adapters own their child process and normalize its protocol into `AgentStateKind` events; hosts never see raw protocol. Bare CLI names (`codex`, `claude`) are resolved through PATH/PATHEXT by `AgentExecutableResolver` in Core, because `Process.Start` with `UseShellExecute=false` does not search PATH on Windows. See [adapters.md](adapters.md) for per-adapter protocol detail and the authoring guide.

The pending-approval contract: an `ApprovalRequired`/`WaitingForInput` event carries `RequestId` (and `SessionId`); the host stores them, arms the mapping engine, and hydrates approval commands with those ids. `Completed`, `Error`, or a `Working` event that carries a `RequestId` clears the pending state.

## Haptics

- `HapticPattern` = named frame list (`RumbleFrame`: low/high motor + left/right trigger, clamped 0..1), optionally looping.
- `HapticScheduler` serializes playback per controller: `PlayAsync` cancels and drains the previous cue, then returns as soon as the new cue is scheduled — looping cues never block the agent event loop.
- `HapticSchedulerHub` sits between consumers (agent loop, GUI preview) and whichever controller is currently attached. Detached = silent no-op; device-loss exceptions are swallowed. This isolates event loops from controller churn.
- Device implementations must zero rumble in a `finally` when playback ends and on dispose. `AgentStateKind.Idle` stops haptics instead of routing a pattern.
- Catalog values are provisional pending real-device validation.

## Desktop GUI

`CtrlAgent.Gui` (Avalonia, compiled bindings, dark neon design system in `App.axaml`) is a thin shell over `HostEngine`:

- `MainViewModel` subscribes to engine events and marshals them onto the UI thread with `Dispatcher.UIThread.Post`; it owns the severity-tinted event stream (`LogEntry`), the binding rows (`BindingRow`, chip labels shared with CTRL·BOT via `ControlLabels`), and the status-dot/pulse state.
- The main window extends its client area into the title bar (hero band = drag handle), mirrors live input on an Elite Series 2 vector (`ControllerVisualViewModel`: pressed > approval-highlight > idle brushes, PS face labels on DualSense), and hosts CTRL·BOT (`AgentBuddyViewModel`: profile-derived coaching, animated moods).
- It is a tray app: closing hides the window (`ShutdownMode.OnExplicitShutdown`); the tray menu restores it, toggles the overlay, or exits. `OverlayWindow` (frameless, topmost) and `ToastWindow` (bottom-right notifications with approve/decline) share the same `MainViewModel`, so every surface stays in sync.
- `ProfileEditorWindow` edits bindings with live `ControllerProfileValidator` feedback and applies via `HostEngine.TryApplyProfile`; `GuiSettings` persists last-used launch options to `%AppData%/CtrlAgent`; a first-run setup overlay collects agent + working directory so no CLI flags are needed.
- **Mainframe mode** (`MainframeWindow` + `MainframeViewModel`) is the fullscreen controller-first UI, modeled on Steam Big Picture: a horizontal tile rail with a neon focus ring (d-pad/left-stick to move, A select, B back), CTRL·BOT center-stage beside a large agent-response feed, a voice-prompt overlay (`SpeechToTextService`, offline System.Speech dictation), a fullscreen shortcuts screen listing every binding in the active profile, and a persistent bottom button legend. Agent actions are deliberately not navigable: they fire from the user's profile bindings exactly as they do outside the mode, and the screen shows those bindings as a read-only HUD, so no destructive action can be reached by wandering focus onto it. Focus exists only for the settings panel. `SetInputCapture` is therefore state-driven — enabled while settings/shortcuts/voice own the screen (the d-pad moves focus rather than switching sessions), disabled otherwise so every binding stays live. Approval bindings in the HUD light amber while a request is pending. Entered from the header rail, F11, the Xbox/PS (Guide) button, or a View double-press; `ControllerControl.Guide` is reported by raw-HID DualSense and by XInput through `XInputGetStateEx` (ordinal 100, probed once with a silent fallback to `XInputGetState`), but never by the GameInput bridge — GameInput reserves the nexus button for the system.

## Threading and lifecycle model

- Each host runs three concurrent loops: **controller sessions**, **agent events**, and **operator input** (console commands or the UI thread).
- The controller-session loop acquires a device, runs it until its stream ends or faults, then detaches haptics, disposes scheduler and device (stopping rumble), and waits for the next device. Controller loss never exits the host.
- Adapters push events through unbounded channels; async streams (`IAsyncEnumerable`) are the only cross-component event surface.
- Every long-running operation takes a `CancellationToken`; Ctrl+C / window close cancels one shutdown token that all loops observe.
- **Prompt queue:** `SubmitPrompt` commands issued while the agent is busy (Working / ApprovalRequired / WaitingForInput) queue in `HostEngine` (max 5) and flush one at a time when the agent settles with no approval pending — the Claude-app behavior. The busy flag is set optimistically at dispatch so a double-tap queues instead of double-sending; `PromptQueueChanged` reports the count to UIs.
- Child processes (Codex, Claude Code) that die are restarted with capped exponential backoff (2 s → 15 s, max 5 attempts). On exit: in-flight requests are failed fast, pending approvals cleared, an `Error` event published (which also plays the error rumble). While down, `ExecuteAsync` publishes `Error` events instead of throwing so host loops keep running.
- Hosts wrap all `ExecuteAsync` calls in a catch-all logger — an adapter failure is an event, never a crash.

## Safety rules

- No default mapping may approve a destructive action with a single accidental face-button press.
- Approval bindings must use a rear paddle, a chord, or a hold gesture, and must be gated on a pending request. This is *enforced by profile validation*, not convention.
- Session-wide approval is a separate command from one-time approval.
- Adapters must surface the exact session and request associated with an approval before accepting a controller response.
- CtrlAgent does not attempt to bypass anti-cheat, access-control, sandbox, or agent-approval systems.
- Keyboard/mouse injection remains out of scope as a primary path.

## Decision log

Decisions made since the original blueprint, with rationale:

1. **Native bridge as a separate process, not in-proc interop.** GameInput is COM-heavy; a crash in native code must not take the host down, and a console exe is independently testable. Stdio JSONL keeps the ABI surface at zero. (Supersedes the deferred "C ABI vs generated interop" question.)
2. **Stdio JSONL everywhere.** Bridge, Codex app-server, and Claude Code all speak newline-delimited JSON over stdio. One transport idiom, no sockets, no server ports.
3. **Gestures resolve from event timestamps.** No timers in `MappingEngine`; hold/double-press math uses the timestamps the device stamped on events. Deterministic, testable, and immune to event-loop jitter. Trade-off: hold fires on release, not at threshold-crossing.
4. **Profiles are validated, not trusted.** `MappingEngine` refuses invalid profiles at construction; JSON loading validates and reports every problem. Approval safety is a validator rule, not a convention.
5. **Avalonia for the GUI.** Chosen over WinUI 3/WPF: fully buildable and verifiable off-Windows, one codebase if handheld/cross-platform frontends happen, Fluent theme out of the box. Pinned to the last 11.x line (11.3.18). (Supersedes the deferred UI-framework question.)
6. **Dependency-free test harness.** A console exe with a `(Name, Func<Task>)` table instead of xUnit — zero packages, trivially debuggable, exits nonzero on failure. Trade-off: no filtering/parallelism; acceptable at current scale.
7. **Restart-with-capped-backoff for child processes.** Uniform pattern across the Codex and Claude adapters (and a defunct-bridge re-resolve on the controller side).
8. **Claude Code integration via stream-json + `--permission-prompt-tool stdio`** rather than MCP or hooks: permission prompts arrive as `can_use_tool` control requests on the same pipe, which maps 1:1 onto the approval paddle flow.
9. **Profile layers activate on device capability, not device identity.** A layer declares `always`/`requiresPaddles`/`withoutPaddles`; the engine follows whatever controller is connected (`SetDeviceCapabilities` on connect, carried across runtime profile swaps). Collision validation runs per capability "world" so mutually exclusive layers may share chords. Identity-based matching (per-VID/PID profiles) stays deferred until a real need shows up.
10. **Session continuity leans on the platforms' own persistence.** Codex threads and Claude Code sessions both survive on disk, so crash recovery and session switching are `thread/resume` / `--resume` calls rather than any state mirrored in CtrlAgent. Trade-off: Claude session switching restarts the CLI process (one session per process).

## Still deferred

- Per-device profile matching by identity (VID/PID/name) — capability layers exist today.
- Multi-controller arbitration.
- Remote or mobile control surfaces.
- Third-party adapter packaging/discovery.

## Primary references

- Microsoft GameInput overview: https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-overview
- Microsoft GameInput gamepad buttons and Elite paddles: https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/enums/gameinputgamepadbuttons
- Microsoft.GameInput NuGet package: https://www.nuget.org/packages/Microsoft.GameInput
- Codex app-server protocol: https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md
- Claude Code CLI reference (stream-json, permission prompt tool): https://code.claude.com/docs/en/cli-reference
