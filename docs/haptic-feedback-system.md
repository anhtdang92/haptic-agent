# CtrlAgent haptic feedback system

Haptics are a primary output channel in CtrlAgent, not decoration. A user should be able to keep attention on the code, television, or another display and still understand the agent's state through the controller.

## Tactile language

Patterns use consistent direction and rhythm:

- rising pulses: accepted, progressing, completed;
- falling pulses: interrupted, canceled, disconnected;
- alternating trigger/side pulses: a decision is required;
- short high-frequency tick: navigation;
- repeated low-frequency block: rejected, queue full, or error;
- sparse heartbeat: background work;
- heavy double impact: failure requiring attention.

## Cue inventory

| Area | Cues |
|---|---|
| System | connected, disconnected |
| Navigation | navigation tick, boundary |
| Commands | accepted, rejected, prompt queued, queue full |
| Agent progress | working heartbeat, thinking ladder, tool started, tool finished |
| Approval | approval required, approve once, approve session, decline |
| Voice | listening heartbeat, recognized, failed |
| Turn lifecycle | waiting for input, interrupted, completed, error |

## Device adaptation

Every frame is transformed immediately before playback:

- master intensity is clamped to `0..1`;
- unavailable low/high-frequency motors are removed;
- unavailable left/right trigger motors are removed;
- a pattern that becomes entirely silent is skipped;
- categories can be disabled without changing the semantic router.

This keeps one semantic cue vocabulary while respecting Xbox, Elite, GameInput, XInput, DualSense, and reduced-capability transports.

## Required event wiring

The completed system must route all of these events, not only agent state transitions:

1. controller connected/disconnected;
2. profile shortcut accepted or blocked;
3. prompt sent, queued, dequeued, or dropped;
4. agent working/thinking/tool activity;
5. approval requested and every approval response;
6. interrupt/cancel;
7. voice listening, recognition success, silence, and provider failure;
8. menu/list/diff navigation and boundaries;
9. session/model/effort/permission changes;
10. final completion and errors.

A command cue must not accidentally silence a persistent approval reminder. The scheduler integration pass must either support persistent plus transient layers or explicitly resume the persistent state after a transient cue.

## Tuning profiles

`HapticSettings` currently exposes:

- `Enabled`;
- `MasterIntensity`;
- `NavigationEnabled`;
- `ProgressEnabled`;
- `ApprovalRemindersEnabled`.

The visual settings experience should offer at least:

- Off;
- Gentle;
- Balanced;
- Strong;
- Custom intensity.

Approval and error cues should remain independently controllable because they carry safety-critical information.

## Validation

For each qualified controller and transport:

- identify every cue blind in a randomized test;
- verify approval, completion, warning, and error are not confused;
- test at 25%, 50%, 75%, and 100% intensity;
- verify trigger-motor fallback does not erase meaning;
- run 100+ cues over 30 minutes;
- interrupt looping cues repeatedly;
- disconnect during a loop and reconnect;
- verify all motors stop on shutdown;
- reject any cue that is painful, fatiguing, or easy to miss.

No software test can establish tactile quality. Physical blind testing is required before calling a pattern tuned or a transport supported.
