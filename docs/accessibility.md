# Accessibility Standard

CtrlAgent is a controller-first Windows application, but controller support is an additional input path—not a substitute for keyboard, pointer, screen-reader, magnification, high-contrast, or reduced-motion access. A feature is not considered complete when it is usable through only one input method.

## Accessibility target

The product targets the intent of WCAG 2.2 Level AA for applicable desktop-software interactions and the Windows accessibility conventions exposed through Avalonia. Conformance must be described narrowly: this document defines the product standard, while a completed evidence report records what was actually tested.

A stable release must not claim “fully accessible” unless the exact release candidate has passed the manual evidence gate in `validation/accessibility/`.

## Non-negotiable requirements

### Keyboard operation

- Every interactive function must be reachable without a controller or pointer.
- Focus order must follow the visual and task order.
- Focus must never become trapped in overlays, panels, lists, the transcript, or the composer.
- `Escape` closes the topmost dismissible surface and returns focus to the control that opened it.
- Dialog-like surfaces place initial focus on a safe control; destructive or approval actions must not receive accidental default focus.
- Multiline prompt editing retains normal text-navigation behavior. Arrow keys used inside the prompt must not trigger global controller-style navigation.
- Keyboard shortcuts supplement normal tab navigation; they never become the only route to an action.

### Visible focus

- Every keyboard-focusable control must have a visible focus indicator.
- The indicator must remain visible in default, hover, selected, disabled-adjacent, dark, and high-contrast contexts.
- Focus must not be communicated by color alone.
- When a surface opens, closes, refreshes, or changes mode, focus must move predictably and must not reset to the window root without reason.

### Names, roles, states, and values

- Icon-only controls require accessible names that describe the action, not the artwork.
- Toggle, selected, expanded, busy, unavailable, invalid, and approval-required states must be exposed programmatically where the framework supports them.
- Controller glyphs and color-coded face buttons require text equivalents.
- Status changes such as connection, working, approval required, completion, interruption, and error must be available as text. Haptics, animation, color, or sound may reinforce the state but may not be the only signal.
- Error messages must name the problem and provide a recovery action.

### Contrast and color

- Normal text and essential icons target at least 4.5:1 contrast against their immediate background.
- Large text targets at least 3:1.
- Focus indicators, control boundaries needed to identify a control, and meaningful graphics target at least 3:1 against adjacent colors.
- Approval, success, warning, error, controller identity, and input availability must never depend on hue alone.
- The product must remain understandable with Windows color filters and in grayscale.

### Text, scaling, and layout

- The primary workflow must remain operable at 200% Windows text/display scaling on a supported minimum viewport.
- Text must not be clipped, overlapped, or hidden behind fixed-position surfaces.
- Horizontal scrolling must not be required for ordinary prose, settings, or approval decisions.
- Zoom or scaling must not remove access to Submit, Interrupt, approval answers, window controls, or recovery actions.
- Truncation requires an accessible full value when the omitted text is needed to make a decision.

### Motion, flashing, and audio

- Essential information must not depend on animation.
- Repeated ambient and decorative motion must respect the operating system's reduced-motion preference when the framework exposes it; otherwise CtrlAgent must provide an application-level reduced-motion option before stable accessibility claims are made.
- Reduced motion disables or substantially shortens boot choreography, ambient sweeps, looping blooms, celebration movement, and nonessential transitions while preserving state changes.
- No content may flash more than three times per second.
- Audio cues require a visible/text equivalent and must not be required to complete a task.

### Screen readers and magnification

The release candidate must be exercised with Windows Narrator. NVDA testing is strongly recommended before public stable release.

At minimum verify:

- window title and current workspace are announced meaningfully;
- setup fields, buttons, menus, lists, transcript items, model/permission controls, and approval actions have useful names and roles;
- reading order matches visual order;
- live status updates are announced without repeatedly interrupting the user;
- the transcript can be reviewed without focus being pulled to every streamed token;
- approval details are available before the approval answers;
- focused controls remain visible under Windows Magnifier;
- no essential tooltip disappears before it can be read.

## Input-equivalence matrix

Every core task must have equivalent routes:

| Task | Keyboard/pointer | Controller | Assistive-technology expectation |
|---|---|---|---|
| Enter Mainframe | Standard button or documented shortcut | Bound controller action | Control has an accessible action name |
| Compose and submit | Focusable multiline editor and Submit action | Voice/preset/controller route where configured | Editor name, value, and submit state are exposed |
| Review changes | Reachable review action and navigable file/line content | Controller review navigation | File names and added/removed status are text, not color-only |
| Answer approval | Reachable named buttons with safe focus behavior | Bound approval controls | Request details precede clearly named answer choices |
| Interrupt work | Reachable named action and shortcut | Bound interrupt control | Busy/interruptible state is conveyed programmatically |
| Change settings | Fully tab-navigable settings surface | Controller navigation where supported | Labels are associated with controls and current values |
| Exit or dismiss | Window controls and `Escape` behavior | Bound back/exit behavior | Focus returns to the invoking context |

## Release evidence gate

For every release candidate, copy `validation/accessibility/accessibility-report.template.md` to a versioned report and complete it using the exact downloaded build.

A stable accessibility result requires all critical checks to pass on:

- keyboard only;
- Windows Narrator;
- Windows high-contrast mode;
- 200% display/text scaling;
- Windows Magnifier;
- reduced-motion configuration;
- color-independent approval, error, success, connection, and controller states.

Any failure that prevents setup, prompt submission, review, approval, interruption, recovery, or exit is a release blocker. Lesser defects must be documented in release notes with a workaround and issue link.

## Development checklist

When changing UI code:

1. Give every icon-only action a durable accessible name.
2. Confirm logical tab order and focus restoration.
3. Ensure text equivalents exist for glyphs, color, audio, haptics, and motion.
4. Check default and high-contrast focus visibility.
5. Test narrow layout and 200% scaling.
6. Verify streamed content does not steal focus or create unusable announcement noise.
7. Update this guide and the evidence template when a new interaction pattern is introduced.
8. Add automated coverage for pure navigation/state logic and a render-harness case for each new surface; retain manual assistive-technology verification because screenshots cannot prove semantics.

## Definition of 10/10

Accessibility reaches 10/10 only when:

- all core workflows are independently operable by keyboard;
- semantic names, roles, states, and values are verified with Narrator;
- focus order, visibility, containment, and restoration pass on every major surface;
- text and controls remain usable at 200% scaling and with Magnifier;
- contrast and non-color communication pass for all semantic states;
- reduced motion is implemented and verified rather than merely documented;
- the exact release candidate has a completed evidence report;
- known failures are fixed or clearly scoped, documented, and non-blocking.

Until that evidence exists, the accurate product statement is: **accessibility work is implemented and under qualification**.
