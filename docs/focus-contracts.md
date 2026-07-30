# Focus Contracts

A Focus Contract controls which semantic events may request the user's attention. It is applied in `CtrlAgent.Core.FeedbackRouter`, after an agent or command has been translated into a semantic haptic event and before a pattern is scheduled.

## Why this belongs in Core

Claude Code, Codex, the mock adapter, Mainframe, the desktop window, and the console host must not each invent notification rules. The adapter reports what happened. Core decides whether that event is important enough to interrupt the selected focus mode. The controller transport then adapts the chosen pattern to the motors it actually supports.

```text
Agent or command event
        |
        v
FeedbackRouter: semantic classification
        |
        v
FocusContract: attention policy
        |
        v
HapticSettings: category and master controls
        |
        v
Controller capability adaptation
        |
        v
Haptic scheduler
```

## API

Select a built-in contract:

```csharp
FocusContractSettings.Select(FocusMode.DeepFocus);
```

Or supply a customized immutable contract:

```csharp
FocusContractSettings.Current = FocusContract.For(FocusMode.ActiveSupervision) with
{
    NotifyToolActivity = false,
    StalledWorkThreshold = TimeSpan.FromMinutes(8),
    IntensityMultiplier = 0.70f,
};
```

The effective playback intensity is:

```text
master intensity × focus-contract multiplier
```

and is clamped to the controller intensity range.

## Built-in contracts

### Deep Focus

Surfaces:

- command acknowledgement;
- waiting for input;
- approval required;
- completion;
- interruption;
- error;
- voice lifecycle;
- controller system events.

Suppresses routine progress, tool activity, and navigation ticks.

### Active Supervision

The default. Surfaces every semantic category and uses a moderate intensity multiplier.

### Silent Watch

Surfaces approval, interruption, and errors. It intentionally suppresses ordinary completion and voice/navigation feedback by default. This mode must never suppress an approval or failure.

### Couch

Surfaces every category at full contract intensity. Intended for operation farther from the screen, where stronger tactile distinction is useful.

### Accessibility

Surfaces every category. This mode is a policy foundation, not a claim of accessibility qualification. Final timing, repetition, multimodal reinforcement, and reduced-sensory variants require user and assistive-technology testing.

## Semantic event mapping

| Event | Attention category |
|---|---|
| Navigation/page movement | Navigation |
| Prompt/model/session/settings command accepted | Command acknowledgement |
| Working heartbeat/thinking | Routine progress |
| Tool/read/search/run activity | Tool activity |
| Agent waiting for an answer | Waiting for input |
| Permission request | Approval required |
| Turn completed | Completed |
| User-requested cancel/interrupt | Interrupted |
| Agent or transport failure | Error |
| Voice listening/transcription result | Voice |
| Controller connect/disconnect | System |

## Safety invariants

1. A Focus Contract is allowed to suppress routine information, not required safety controls.
2. Built-in modes must not suppress agent errors.
3. Built-in modes must not suppress approval requests.
4. Suppressing a haptic cue must not suppress the underlying UI event, log entry, or command.
5. A contract change affects future routing; it must not mutate adapter state.
6. Intensity multipliers must be clamped before reaching a device.
7. Metrics must not record content, paths, identifiers, or tool arguments.

Custom contracts may technically disable any category, but the UI should warn before permitting approval or error cues to be disabled.

## Attention metrics

`FeedbackRouter.Metrics` exposes privacy-preserving counters through `AttentionMetricsSnapshot`:

```csharp
var snapshot = router.Metrics.Snapshot();
```

Current counters:

- haptic notifications delivered;
- routine notifications suppressed;
- approval requests surfaced;
- approval responses handled;
- completions surfaced;
- errors surfaced;
- autonomous `Working` time observed.

`AvoidedRoutineInterruptions` is currently equal to routine notifications suppressed. It is a product-development metric, not a scientifically validated count of human context switches.

## Required tests

Before merging the feature as complete, add automated coverage for:

- every built-in contract's allowed/suppressed categories;
- approval and error events surviving every built-in mode;
- Deep Focus suppressing routine working and tool events;
- Silent Watch suppressing completion but not interruption;
- command approval responses incrementing metrics;
- working-time accumulation across state transitions;
- intensity multiplier clamping;
- custom-contract replacement;
- no pattern produced when both the contract and haptic category settings suppress it.

## Required product integration

The Core implementation is only the policy foundation. A complete user experience still requires:

- a Focus Mode selector in Mainframe and desktop settings;
- persistence in `%AppData%/CtrlAgent/settings.json`;
- controller-bindable mode cycling;
- a concise explanation of what each mode will interrupt;
- an optional attention-saved summary;
- a warning before disabling approval/error feedback;
- layered haptic scheduling so transient navigation cues resume persistent approval/progress states instead of replacing them permanently.
