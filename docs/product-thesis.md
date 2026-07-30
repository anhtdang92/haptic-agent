# Product thesis: tactile AI supervision

## One-sentence positioning

**CtrlAgent lets people feel, supervise, and safely control autonomous AI agents without constantly watching their windows.**

Consumer-facing expression:

> Stop babysitting your AI agent. Feel when it needs you.

Technical expression:

> CtrlAgent is an agent-independent tactile control and permission layer for autonomous software development.

## The problem

AI coding agents can work for meaningful periods without direct input, but their users still have to monitor terminals, watch application windows, and repeatedly break concentration to discover whether the agent:

- is still working;
- changed from planning to tool execution;
- needs approval;
- is waiting for an answer;
- failed;
- was interrupted;
- or completed successfully.

Visual notifications move the problem rather than solve it: they still ask for the user's eyes and attention. CtrlAgent exists to reduce that supervision burden while preserving deliberate human control.

## The product category

CtrlAgent is not primarily "coding with a gamepad." It creates the category **tactile AI supervision**.

The controller is the first interface because it combines:

- multiple independent haptic channels;
- physical shortcut memory;
- analog controls;
- deliberate chords;
- wireless use;
- eyes-free feedback;
- and an input device the user can keep nearby while focused elsewhere.

The controller is therefore both an input surface and an ambient agent-state display.

## Three-part promise

### 1. Feel what the agent is doing

Agent-independent events are translated into a learned tactile language:

- rising patterns mean progress or success;
- falling patterns mean interruption or cancellation;
- alternating sides mean a decision is required;
- short high-frequency ticks mean navigation;
- heavy repeated impacts mean failure;
- persistent low-intensity patterns communicate continuing states.

The semantic meaning remains stable across Claude Code, Codex, and future agents. Hardware adapters may express the same cue differently through Xbox motors, Elite trigger motors, DualSense actuators, or adaptive triggers.

### 2. Respond without changing focus

A user can approve, decline, interrupt, queue instructions, dictate a prompt, review status, and switch sessions without finding the agent's window first.

### 3. Trust what was executed

CtrlAgent is a physical trust boundary:

- dangerous actions require deliberate gestures;
- approval controls are inert when no request is pending;
- non-approval commands are locked out during an approval decision;
- the requested change can be reviewed before approval;
- accepted, declined, interrupted, and failed outcomes have distinct confirmations;
- duplicate and fall-through command routes are treated as safety defects.

## Focus principle

Every feature must answer this question:

> Does this help the user understand or control autonomous AI work while spending less attention on the AI interface?

Features that satisfy the principle include haptics, approvals, voice input, concise diff review, session control, interruption, risk communication, and short status summaries.

CtrlAgent must not become a second text editor, terminal emulator, or complete replacement for the underlying agent. It supervises and controls the agent rather than duplicating its entire interface.

## Focus Contracts

A Focus Contract is the explicit agreement between the user and autonomous work about what is allowed to interrupt them.

Built-in modes:

| Mode | Intended behavior |
|---|---|
| Deep Focus | Suppress routine progress and navigation; surface approvals, waiting input, completion, interruption, and errors |
| Active Supervision | Communicate normal progress, tool activity, commands, decisions, and results |
| Silent Watch | Surface only approval, interruption, and failure by default |
| Couch | Stronger, comprehensive tactile communication for screen-distance operation |
| Accessibility | Highly explicit, slower-to-tune multimodal operation with all semantic categories available |

The policy is enforced in Core's `FeedbackRouter`, making it consistent across every agent adapter and application surface.

## Attention saved

CtrlAgent should report value in terms aligned with its purpose, not engagement. Privacy-preserving metrics may include:

- autonomous work time observed;
- routine notifications suppressed by the Focus Contract;
- approval requests surfaced;
- approval responses handled through CtrlAgent;
- completions and failures surfaced tactically;
- prompts queued while the agent was busy.

No prompt text, file path, tool arguments, agent output, controller identifier, or account information is required for these counters.

The eventual user-facing summary should communicate outcomes such as:

> CtrlAgent protected 42 minutes of uninterrupted work and handled 6 agent decisions this week.

That wording must remain evidence-based: "attention saved" is an estimate until a validated measurement model exists.

## Defensible product system

CtrlAgent's moat is the system, not basic gamepad support:

1. a documented, learned tactile language;
2. one semantic command model across agents;
3. hardware-adaptive tactile rendering;
4. central permission and approval safety policy;
5. personal tactile and Focus Contract profiles;
6. evidence-backed device and transport qualification;
7. privacy-preserving attention metrics.

## Canonical demonstration

1. The user asks an agent to implement a feature.
2. CtrlAgent begins a subtle working state.
3. Tool execution changes the tactile signature.
4. The user moves attention elsewhere.
5. Alternating trigger/side feedback announces an approval request.
6. The user reviews the proposed change and deliberately approves it.
7. CtrlAgent confirms that the approval was accepted.
8. A rising completion signature announces the finished turn.
9. One command opens the resulting diff.

The emotional payoff is immediate:

> I do not have to babysit the agent, but I remain in control.

## Product boundaries

Do not lead with these descriptions:

- coding with a game controller;
- Claude Code for the couch;
- rumble notifications for AI;
- a fun way to vibe code.

They may describe demonstrations, but they make the product appear narrower than its purpose.

The durable identity is:

> **The tactile supervision layer for autonomous AI agents.**
