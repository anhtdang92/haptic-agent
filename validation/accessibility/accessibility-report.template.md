# CtrlAgent Accessibility Evidence

Complete this report using the exact release-candidate artifacts downloaded from GitHub. Record observations, not assumptions. Do not include usernames, machine names, private workspace contents, credentials, controller identifiers, or full private paths.

## Build and environment

- Version:
- Commit:
- Artifact and SHA-256:
- Test date:
- Tester:
- Windows edition/build:
- Display resolution:
- Display scaling:
- Text scaling:
- Avalonia/runtime version if known:
- Assistive technologies and versions:

## Critical workflow results

Use `PASS`, `FAIL`, or `BLOCKED`. Every non-pass result requires notes and an issue link.

| Workflow | Keyboard only | Narrator | Magnifier / 200% | High contrast | Reduced motion | Notes / issue |
|---|---|---|---|---|---|---|
| First-run setup |  |  |  |  |  |  |
| Select workspace and agent |  |  |  |  |  |  |
| Enter and exit Mainframe |  |  |  |  |  |  |
| Compose multiline prompt |  |  |  |  |  |  |
| Submit and interrupt |  |  |  |  |  |  |
| Read streaming response |  |  |  |  |  |  |
| Review multi-file diff |  |  |  |  |  |  |
| Answer each approval choice |  |  |  |  |  |  |
| Open and close settings |  |  |  |  |  |  |
| Recover from startup error |  |  |  |  |  |  |
| Exit application completely |  |  |  |  |  |  |

## Keyboard and focus

| Check | Result | Evidence / notes |
|---|---|---|
| All interactive controls reachable by Tab/Shift+Tab |  |  |
| Focus order follows task and visual order |  |  |
| Visible focus on every interactive control |  |  |
| Focus is not trapped in overlays, lists, transcript, or composer |  |  |
| Escape dismisses the topmost dismissible surface |  |  |
| Dismissal restores focus to the invoking control |  |  |
| Multiline arrow-key editing does not trigger global navigation |  |  |
| Approval surface initially focuses a safe, non-destructive location |  |  |
| Refresh and streaming updates do not steal focus |  |  |
| Keyboard shortcuts have an ordinary navigable alternative |  |  |

## Screen-reader semantics

Test with Windows Narrator; add NVDA results when available.

| Check | Narrator | NVDA | Evidence / notes |
|---|---|---|---|
| Window and workspace title announced meaningfully |  |  |  |
| Icon-only controls have useful action names |  |  |  |
| Form labels are associated with their controls |  |  |  |
| Toggles, selections, expanded state, busy state, and disabled state announced |  |  |  |
| Transcript reading order is logical |  |  |  |
| Streaming does not announce every token or continuously interrupt reading |  |  |  |
| Approval request details are announced before answer choices |  |  |  |
| Diff rows expose file and added/removed meaning without relying on color |  |  |  |
| Errors name the problem and recovery action |  |  |  |
| Connection, working, completion, interruption, and error states are available as text |  |  |  |

## Visual accessibility

| Check | Result | Evidence / notes |
|---|---|---|
| Normal text contrast is at least 4.5:1 |  |  |
| Large text contrast is at least 3:1 |  |  |
| Essential icons, boundaries, and focus indicators are at least 3:1 |  |  |
| Approval/success/warning/error states remain distinct in grayscale |  |  |
| Windows color filters preserve meaning |  |  |
| High-contrast mode preserves controls, content, selection, and focus |  |  |
| 200% scaling introduces no clipped or overlapping critical content |  |  |
| No ordinary workflow requires horizontal scrolling |  |  |
| Magnifier keeps the focused control visible |  |  |
| Full decision text is available when visible text is truncated |  |  |

## Motion, sound, and haptics

| Check | Result | Evidence / notes |
|---|---|---|
| Reduced-motion mode disables or shortens nonessential animation |  |  |
| Boot sequence remains understandable with motion disabled |  |  |
| No content flashes more than three times per second |  |  |
| Animation is never the only indication of state |  |  |
| Audio cues have text/visual equivalents |  |  |
| Haptic cues have text/visual equivalents |  |  |
| User can complete critical workflows with sound muted and no controller |  |  |

## Input equivalence

| Task | Keyboard/pointer route | Controller route | Semantics verified | Result / notes |
|---|---|---|---|---|
| Enter Mainframe |  |  |  |  |
| Submit prompt |  |  |  |  |
| Review changes |  |  |  |  |
| Answer approval |  |  |  |  |
| Interrupt |  |  |  |  |
| Change settings |  |  |  |  |
| Dismiss/exit |  |  |  |  |

## Defects and exceptions

| Severity | Issue | Affected users/workflow | Workaround | Release blocker? |
|---|---|---|---|---|
|  |  |  |  |  |

Severity guidance:

- **Critical:** blocks setup, prompt submission, review, approval, interruption, recovery, or exit for an input/assistive-technology path.
- **Major:** causes lost context, inaccessible state, severe focus failure, or unreadable critical content but has a reliable workaround.
- **Minor:** localized friction that does not prevent task completion.

## Decision

- Keyboard-only critical workflows: `PASS` / `FAIL`
- Narrator critical workflows: `PASS` / `FAIL`
- High contrast: `PASS` / `FAIL`
- 200% scaling and Magnifier: `PASS` / `FAIL`
- Reduced motion: `PASS` / `FAIL`
- Color-independent semantic states: `PASS` / `FAIL`
- Critical accessibility defects open: `YES` / `NO`
- Accessibility qualification: `PASS` / `FAIL`

Approval notes:
