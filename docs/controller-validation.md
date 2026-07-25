# Controller Validation Plan

This plan answers the hardware questions software builds cannot. The primary target is the Xbox Elite Series 2 (tests 1–6 and the guided wizard below); a first-pass DualSense checklist follows at the end.

## Target hardware

Primary device:

- Xbox Elite Wireless Controller Series 2

Connection matrix:

| Transport | Priority | Result |
|---|---:|---|
| USB-C cable | 1 | **Partially tested 2026-07-24** — discovery, standard buttons, and initial state verified through the bridge; paddles are never reported (see Test 3 findings) |
| Xbox Wireless Adapter | 2 | Not tested |
| Bluetooth | 3 | **Tested 2026-07-24** — the GameInput redistributable does not enumerate Bluetooth Xbox controllers at all (`GetCurrentReading` fails with `0x838A0003` DEVICE_NOT_FOUND); the XInput fallback works fully over Bluetooth |

USB is first because it removes wireless pairing and transport variables.

Two cross-transport findings from the 2026-07-24 session (Windows 11 26200, GameInput redistributable 3.x, Elite Series 2 on current firmware):

- **Focus gating:** GameInput delivers state changes only while a window owned by the reading process tree has focus. The bridge run as a windowless child received the connect event and initial snapshot but no input until its host application (the GUI) was focused. Console-window wrappers (`cmd`, terminals) do not count — the foreground window belongs to the terminal process, not the bridge.
- **Steam:** Steam's "Xbox Controller Enhanced Features" driver was present during testing; exiting Steam changed nothing in these results, but keep it in mind when reproducing.

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

**Findings 2026-07-24 (USB, Windows 11 26200, GameInput redistributable 3.x):** outcome is (3)/(4) in every configuration — the paddles are never independently visible on PC.

- Default profile (no slot light): paddles are firmware-mapped to A/B/X/Y and arrive as those face buttons only. No paddle flag is ever set.
- Dedicated profile with all four paddles unmapped (primary and shift): paddles transmit **nothing** — no `GameInputGamepadPaddle*` flag, and no bit in the raw 18-slot controller-button array (`GetControllerButtonState`), while a control press of A registers normally in both views.
- Conclusion: the paddle flags in the GameInput API are not populated by the PC redistributable for the Elite Series 2; remapping happens entirely inside controller firmware. The bridge therefore reports `hasFourPaddles: false`, which activates the `withoutPaddles` XInput-style chord layer. Practical paddle use on PC is limited to mirroring standard controls via an Xbox Accessories profile (e.g. mapping paddles to otherwise-unused controls such as thumbstick clicks and binding those with hold gestures for approvals).

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

## Guided wizard

`dotnet run --project src/CtrlAgent.App/CtrlAgent.App.csproj -- --validate` walks tests 1–5 interactively (plus a shortened 60-second soak), records paddle and rumble observations, and writes the evidence report in the format below with a computed go/no-go recommendation. The full 30-minute soak in test 6 remains manual.

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

## DualSense verification (first pass)

The DualSense adapter reads raw HID reports directly; the byte layout is community-documented and has **not** been verified on real hardware. On a real DualSense (and, separately, a DualSense Edge), over both USB and Bluetooth:

- confirm every button, stick, trigger, and the touchpad click parse correctly (USB input report `0x01`, Bluetooth `0x31`);
- confirm rumble output and the cyan lightbar apply (output report `0x02` USB / `0x31` Bluetooth with CRC32);
- confirm adaptive-trigger resistance: while an approval is pending the trigger pulls should stiffen in alternating pulses (continuous-resistance effect, mode `0x01`), and release cleanly when the approval resolves or the pad disconnects;
- on the Edge, confirm the rear paddles and Fn buttons surface as the four paddle controls;
- confirm disconnect/reconnect recovers without restarting the host, and that rumble always stops on disconnect.

Record results in `validation/<date>-dualsense-<transport>.md` using the same evidence format as the Elite reports. Do not commit serial numbers or Bluetooth addresses.
