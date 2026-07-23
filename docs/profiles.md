# Controller Profile Reference

Profiles map physical controller input to agent commands. They are versioned JSON documents, validated on every load — an invalid or unsafe profile never runs.

## Quick start

```powershell
# Write the built-in default profile as a starting point
dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- --export-profile my-profile.json

# Run with it (console host or GUI)
dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- --agent mock --profile my-profile.json
```

## Document shape

```json
{
  "version": 1,
  "name": "my-profile",
  "bindings": [
    { "control": "a", "gesture": "press", "command": "submitPrompt" },
    { "control": "x", "gesture": "doublePress", "command": "reviewChanges" },
    {
      "control": "a",
      "gesture": "press",
      "modifiers": ["leftShoulder"],
      "command": "submitPrompt",
      "text": "Run the test suite and fix any failures."
    },
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

`version` must be `1`. Names are case-insensitive; unknown fields are ignored; comments and trailing commas are tolerated.

## Binding fields

| Field | Type | Default | Meaning |
|---|---|---|---|
| `control` | string | required | A `ControllerControl` name: `a`, `b`, `x`, `y`, `menu`, `view`, `dPadUp/Down/Left/Right`, `leftShoulder`, `rightShoulder`, `leftThumbstickButton`, `rightThumbstickButton`, `leftTrigger`, `rightTrigger`, `leftThumbstickX/Y`, `rightThumbstickX/Y`, `paddleLeft1/2`, `paddleRight1/2` |
| `gesture` | string | `press` | One of the gestures below |
| `command` | string | required | An `AgentCommandKind`: `submitPrompt`, `interrupt`, `approveOnce`, `approveForSession`, `decline`, `cancel`, `newSession`, `nextSession`, `previousSession`, `reviewChanges` |
| `modifiers` | string[] | none | Controls that must be held for this binding to match (turns it into a chord) |
| `minimumValue` | number | 0.5 | `axisThreshold` only: absolute axis value that triggers the binding (0 < v ≤ 1) |
| `text` | string | none | `submitPrompt` only: overrides the default prompt |
| `requiresPendingApproval` | bool | false | Binding fires only while an approval request is pending; the command is hydrated with the pending session/request ids |
| `holdMilliseconds` | int | 400 | `tap`/`hold` threshold |
| `doublePressMilliseconds` | int | 300 | `doublePress` window |

## Gestures

| Gesture | Fires on | Semantics |
|---|---|---|
| `press` | button down | Immediate |
| `release` | button up | Immediate |
| `tap` | button up | Held **shorter** than `holdMilliseconds` |
| `hold` | button up | Held **at least** `holdMilliseconds` (note: fires on release, not at the threshold crossing) |
| `doublePress` | second button down | Two presses within `doublePressMilliseconds`; a completed double resets the sequence, so a third press starts a new pair |
| `axisThreshold` | axis change | Fires when the absolute value **crosses** `minimumValue` from below; latched until the axis drops back under the threshold, so jitter above it never re-fires |

Durations are computed from the timestamps stamped on device events, so gesture behavior is identical in tests and on hardware.

### Matching pipeline

For each input event: structural matches are collected → only bindings with the **highest modifier count** survive (chords beat plain buttons) → ineligible approval bindings are dropped. The order matters: an approval chord that is structurally matched but ineligible (no pending request) suppresses the plain-button binding underneath it rather than falling through.

## Validation rules

Loading (and `MappingEngine` construction) rejects the profile with a full list of problems if any rule fails:

**Ambiguity**

- No duplicate binding (same control + gesture + modifier set).
- `press` may not be combined with `tap`, `hold`, or `doublePress` on the same chord (one physical action would fire twice). Use `tap` instead of `press`.
- `tap` may not be combined with `doublePress` on the same chord (the first tap of the double would fire both).
- `tap` + `hold` on the same chord is allowed and is the intended way to overload a button.

**Sanity**

- `axisThreshold` needs `minimumValue` in (0, 1]; hold/double-press durations must be positive.

**Approval safety**

- `approveOnce`, `approveForSession`, and `decline` must set `requiresPendingApproval`.
- `approveOnce` and `approveForSession` must additionally be *deliberate*: bound to a paddle, a chord (≥1 modifier), or a `hold` gesture. A bare face-button press can never approve anything, even with the pending flag set.
- `cancel` is exempt from the deliberateness rule (it is never destructive).

## Default profile

A: submit prompt · B: interrupt · X: review changes · Menu: new session · D-pad left/right: previous/next session · LB+A: "run tests and fix failures" prompt · paddles: approve once / approve for session / decline / cancel · RB+A/Y/X/B: the same four approvals as XInput fallback chords. All approval bindings are pending-gated.

Export it with `--export-profile` to see the exact JSON.
