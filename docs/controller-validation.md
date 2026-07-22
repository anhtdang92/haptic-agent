# Xbox Elite Controller Validation Plan

This spike answers the hardware questions before we build a UI or a real agent integration.

## Target hardware

Primary device:

- Xbox Elite Wireless Controller Series 2

Connection matrix:

| Transport | Priority | Result |
|---|---:|---|
| USB-C cable | 1 | Not tested |
| Xbox Wireless Adapter | 2 | Not tested |
| Bluetooth | 3 | Not tested |

USB is first because it removes wireless pairing and transport variables.

## Required environment

- Windows 10 19H1 or newer
- Current controller firmware through Xbox Accessories
- .NET 10 SDK 10.0.302 or compatible later feature band
- Visual Studio 2026 with Desktop development with C++
- Microsoft.GameInput 3.4.259

Record the exact Windows build, controller firmware, GameInput package version, and connection transport with every result.

## Test 1: discovery and identity

Pass conditions:

- The controller is discovered when connected before launch.
- The controller is discovered when connected after launch.
- Device information is stable enough to distinguish the intended controller.
- Disconnect and reconnect do not require restarting the process.

Record:

- display name;
- vendor and product IDs when available;
- firmware and hardware versions when available;
- supported input kinds;
- reported gamepad layout and modules;
- supported rumble motors.

## Test 2: standard controls

Confirm independent readings for:

- A, B, X, Y;
- menu and view;
- D-pad directions;
- left and right shoulders;
- left and right thumbstick clicks;
- both triggers as analog values;
- both thumbsticks as X/Y analog values.

The logger should print only state changes so the output remains readable.

## Test 3: four Elite paddles

Confirm the following flags independently:

- PaddleLeft1
- PaddleLeft2
- PaddleRight1
- PaddleRight2

Run this test with a dedicated Xbox Accessories profile. Record whether a paddle produces:

1. only its paddle flag;
2. its paddle flag plus the button assigned in Xbox Accessories;
3. only the assigned standard button;
4. no reading.

Repeat for every transport. This behavior determines whether CtrlAgent can safely treat the paddles as independent controls or needs mapping guidance in its setup wizard.

## Test 4: simultaneous input

Verify that the input stream preserves combinations needed by the mapping engine:

- paddle plus face button;
- paddle plus shoulder;
- two paddles together;
- trigger threshold plus button;
- stick direction plus paddle.

No combination should produce stuck-state events after release.

## Test 5: rumble

Play and identify these outputs:

| Pattern | Intended motors |
|---|---|
| Low thump | Low-frequency |
| Sharp tick | High-frequency |
| Left confirmation | Left trigger |
| Right confirmation | Right trigger |
| Completion | Two short mixed pulses |
| Error | One strong long mixed pulse |

Validate:

- intensity values at 0.25, 0.50, 0.75, and 1.00;
- stop behavior after each pattern;
- behavior when the application loses focus;
- behavior after disconnect and reconnect;
- whether trigger-rumble values are native or adapted into the main motors.

GameInput applies rumble only while the application is in focus, so the final tray application will need a deliberate focus/background strategy rather than assuming vibration always works.

## Test 6: sustained operation

Run the logger for at least 30 minutes while:

- repeatedly pressing all controls;
- disconnecting and reconnecting once;
- switching Xbox Accessories profiles;
- playing at least 100 short haptic cues.

Pass conditions:

- no crash;
- no unreleased controls;
- no rumble left running;
- no unbounded memory growth;
- input remains responsive after reconnect.

## Evidence format

Save one report per transport under:

```text
validation/<date>-elite-series-2-<transport>.md
```

Each report should include:

- environment and versions;
- pass/fail table;
- raw paddle observations;
- rumble observations;
- known anomalies;
- go/no-go recommendation.

Do not commit serial numbers, Bluetooth addresses, account names, or other unique device identifiers.

## Go/no-go gates

Proceed to the managed controller adapter when:

- USB reliably exposes all required standard controls;
- all four paddles are independently distinguishable, or a documented controller-profile configuration makes them distinguishable;
- at least two clearly different rumble cues work reliably;
- reconnect behavior is recoverable without process restart.

If paddles are not independently available, continue the project with standard controls and rumble, but mark Elite paddle support experimental until a validated raw-report path is available.
