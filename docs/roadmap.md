# CtrlAgent Roadmap

The project advances through evidence-based gates: each phase produces something testable before the next layer is added. This file carries two views — the phase ledger (what the gates were and where they stand) and the prioritized near-term backlog (what to pick up next).

## Snapshot (2026-07-22)

Implemented and unit-tested: platform-independent core, mapping engine with gestures and validated JSON profiles, haptic scheduler + hub, XInput fallback, native GameInput bridge (code complete), Mock/Codex/Claude Code adapters with crash restart, console host with reconnect resilience, Avalonia GUI, hardware validation wizard, Windows CI. 16 tests passing.

**The critical path is now evidence, not code.** Nothing hardware- or agent-facing has been verified against a real Elite controller, a real Codex install, or a real Claude Code install.

## Near-term backlog (priority order)

| # | Item | Why | Status |
|---|---|---|---|
| 1 | Hardware validation runs (`--validate`, per transport) | Gates the paddle decision and haptic tuning; everything Elite-specific is provisional until this exists | **Blocked on hardware access** |
| 2 | Live verification of Codex + Claude Code adapters | Wire shapes follow documented protocols; first real run is the compatibility test | Blocked on installed CLIs |
| 3 | Shared hosting layer (`CtrlAgent.Hosting`) | App and Gui ran parallel copies of the same loops | **Done** — both hosts run on `HostEngine`, covered by an end-to-end test |
| 4 | Claude Code approve-for-session | Wire session-wide permission rules (`updatedPermissions`) instead of degrading to approve-once | **Done** — session-scoped tool allow rule, wire shape unit-tested |
| 5 | GUI phase 2: tray icon, minimize-to-tray, mapping editor with live validation | Phase 5 exit gate needs profile editing without hand-written JSON | **Done** — tray + hide-on-close + editor with live validation, runtime apply, JSON save/load |
| 6 | Release packaging: self-contained win-x64 zip (App + Gui + bridge) on tags | Install without a dev environment | **Done** — release workflow on `v*` tags |
| 7 | Haptic pattern tuning pass | Depends on #1 evidence | Blocked on #1 |
| 8 | Session navigation (Codex threads, Claude `--resume`) | Next/PreviousSession were stubs | **Mostly done** — mock cycles sessions, Codex cycles live threads (D-pad switching works); remaining: Codex `thread/resume` after crash, Claude `--resume` design |
| 9 | Profile layers and per-device matching | Deferred from Phase 3 | Design first |
| 10 | OpenCode / generic process adapter | Phase 6 candidates | After #2 proves the pattern |
| 11 | PS5 DualSense adapter (raw HID) | First non-Xbox controller proves the pluggable story; community-documented HID format, no bridge process needed | Design ready; build after #1 hardware evidence |
| 12 | Cursor adapter (`cursor-agent` CLI) | Third real agent platform | Blocked on protocol research against an installed CLI |
| 13 | Antigravity adapter | Fourth platform target | Blocked on a public automation surface existing |

## Phase ledger

### Phase 0 — Architecture and core contracts — **Complete**

Contracts, haptic model, feedback cues, architecture doc, validation plan, mock demo. Exit gate met.

### Phase 1 — Native GameInput hardware spike — **Code complete, awaiting hardware validation**

The bridge builds in CI and implements discovery, paddle flags, and four-channel rumble. Exit gate (reliable USB input, documented paddle behavior, distinct cues, clean reconnect) **requires real-device evidence** — run `--validate` per transport and commit the reports under `validation/`.

### Phase 2 — Managed GameInput adapter — **Complete in software, pending hardware sign-off**

Bridge client, `IControllerDevice`, serialized cancellable haptics, capability reporting, normalization/scheduling tests, defunct-bridge re-resolution.

### Phase 3 — Mapping engine — **Largely complete**

Delivered: versioned JSON profiles, press/release/tap/hold/double-press/axis-threshold gestures, chords, collision detection, enforced approval safeguards, import/export. Remaining: layers, per-device match criteria, haptic cue overrides in profiles.

### Phase 4 — Codex adapter — **Implemented, pending live verification**

Owned app-server process, handshake, thread/turn lifecycle, approval correlation, interrupt/approve/decline commands, crash restart with backoff. Remaining: verification against a real Codex install; schema pinning; session navigation.

### Phase 5 — Windows desktop application — **In progress**

Delivered (Avalonia): device/agent status, approval actions, prompt submission, haptic preview, event log, tray icon with hide-on-close, and a mapping editor with live validation, runtime profile apply, and JSON save/load. Remaining for the exit gate: Windows notifications, validation-wizard integration, startup settings.

### Phase 6 — Additional adapters — **Claude Code delivered (pulled forward)**

Target: all popular controllers × all popular agentic coding platforms. The launch pair is Xbox Elite + Codex; everything else plugs into the same two interfaces.

**Agent platform targets**

| Platform | Path | Status |
|---|---|---|
| Codex | app-server JSONL over stdio | Implemented, live verification pending |
| Claude Code | stream-json + `--permission-prompt-tool stdio` | Implemented, live verification pending |
| Cursor | `cursor-agent` CLI headless mode; JSON output exists — approval/permission wire protocol needs research | Planned |
| Google Antigravity | No public automation protocol known yet (VS Code-fork agent manager); watch for CLI/extension/MCP surface | Research |
| OpenCode / generic process adapters | stdio JSONL, same shape as existing adapters | Candidate |

**Controller targets**

| Controller | Path | Status |
|---|---|---|
| Xbox family | XInput (done) + GameInput bridge for Elite paddles (done, hardware validation pending) | Supported |
| PS5 DualSense | Raw HID input reports + HID output for rumble/lightbar (community-documented format; no OS driver needed) | Planned next |
| DualSense Edge | Same HID path; rear paddles map to the existing paddle controls | After DualSense |
| Generic pads | SDL2 or GameInput enumeration | Candidate |

Stream Deck / handheld / mobile frontends remain later-stage candidates.

## Release targets

- **v0.1.0 — Hardware proof:** software is ready (`--validate` is the inspector/test utility); tag once per-transport validation reports exist. ← *next release*
- **v0.2.0 — Mock two-way loop:** functionality complete today (profiles + physical feedback via mock agent); tag alongside v0.1 evidence.
- **v0.3.0 — Agent technical preview:** Codex and Claude Code verified against live installs; known protocol gaps closed.
- **v0.5.0 — Usable Windows preview:** tray app, mapping editor, starter profiles, packaged zip, validation wizard integrated in GUI.
- **v1.0.0 — Stable controller-agent interface:** documented extension contracts, dependable upgrades, tested transports, stable profile compatibility.
