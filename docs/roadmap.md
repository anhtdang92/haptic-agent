# CtrlAgent Roadmap

The project advances through evidence-based gates: each phase produces something testable before the next layer is added. This file carries two views — the phase ledger (what the gates were and where they stand) and the prioritized near-term backlog (what to pick up next).

## Snapshot (2026-07-24)

Implemented and unit-tested: platform-independent core, mapping engine with gestures, capability-activated profile layers, and validated JSON profiles, haptic scheduler + hub, XInput fallback, native GameInput bridge (code complete), DualSense raw-HID adapter (protocol unit-tested), Mock/Codex/Claude Code adapters with crash restart and session resume, shared `HostEngine` hosting layer, console host with reconnect resilience, Avalonia GUI (tray, overlay HUD, toasts, first-run setup, profile editor, live Elite mirror, CTRL·BOT, motion polish, fullscreen Big Picture mode with controller navigation, voice prompts, and a shortcuts screen), hardware validation wizard, release packaging on tags, Windows CI. 25 tests passing.

**The critical path is evidence, and it has started landing.** Verified so far: the Claude Code approval loop end to end on a live CLI (2.1.150), and first Elite Series 2 hardware findings — USB input works through the bridge, but the PC GameInput redistributable never reports paddles and does not enumerate Bluetooth Xbox controllers (the XInput fallback covers Bluetooth). Still unverified: Codex against a live install, formal per-transport `--validate` reports, DualSense on a real pad, and the haptic tuning pass.

## Near-term backlog (priority order)

| # | Item | Why | Status |
|---|---|---|---|
| 1 | Hardware validation runs (`--validate`, per transport) | Gates the paddle decision and haptic tuning; everything Elite-specific is provisional until this exists | **Partial evidence 2026-07-24**: Bluetooth = GameInput unsupported (XInput fallback verified); USB = input verified, paddles never reported by the PC redist (bridge now reports no paddles; see `docs/controller-validation.md`). Formal `--validate` reports still to be run |
| 2 | Live verification of Codex + Claude Code adapters | Wire shapes follow documented protocols; first real run is the compatibility test | **Claude Code verified 2026-07-24** (CLI 2.1.150: session init, prompt turns, controller-answered approve and deny, error events; found and fixed the Windows bare-name spawn bug — see `docs/adapters.md`). Codex still blocked on an installed CLI |
| 3 | Shared hosting layer (`CtrlAgent.Hosting`) | App and Gui ran parallel copies of the same loops | **Done** — both hosts run on `HostEngine`, covered by an end-to-end test |
| 4 | Claude Code approve-for-session | Wire session-wide permission rules (`updatedPermissions`) instead of degrading to approve-once | **Done** — session-scoped tool allow rule, wire shape unit-tested |
| 5 | GUI phase 2: tray icon, minimize-to-tray, mapping editor with live validation | Phase 5 exit gate needs profile editing without hand-written JSON | **Done** — tray + hide-on-close + editor with live validation, runtime apply, JSON save/load |
| 6 | Release packaging: self-contained win-x64 zip (App + Gui + bridge) on tags | Install without a dev environment | **Done** — release workflow on `v*` tags |
| 7 | Haptic pattern tuning pass | Depends on #1 evidence | Blocked on #1 |
| 8 | Session navigation (Codex threads, Claude `--resume`) | Next/PreviousSession were stubs | **Done** — mock cycles sessions; Codex cycles threads and resumes the active thread after a crash (`thread/resume`); Claude cycles sessions via process restart with `--resume` and resumes after a crash. Live verification pending with #2 |
| 9 | Profile layers and per-device matching | Deferred from Phase 3 | **Done (first pass)** — named layers with capability activation (`always`/`requiresPaddles`/`withoutPaddles`), collision checks scoped to co-active layers, engine follows the connected device. Richer match criteria (per-device id/name) remain future work |
| 10 | OpenCode / generic process adapter | Phase 6 candidates | After #2 proves the pattern |
| 11 | PS5 DualSense adapter (raw HID) | First non-Xbox controller proves the pluggable story | **Implemented** — protocol unit-tested (USB/BT parse, Edge paddles, CRC); needs a real pad to verify |
| 12 | Cursor adapter (`cursor-agent` CLI) | Third real agent platform | Blocked on protocol research against an installed CLI |
| 13 | Antigravity adapter | Fourth platform target | Blocked on a public automation surface existing |

## Phase ledger

### Phase 0 — Architecture and core contracts — **Complete**

Contracts, haptic model, feedback cues, architecture doc, validation plan, mock demo. Exit gate met.

### Phase 1 — Native GameInput hardware spike — **Partially validated; paddle outcome resolved negatively**

The bridge builds in CI and implements discovery, capability reporting, and four-channel rumble. First real-device evidence (2026-07-24): USB discovery/input verified; **the PC GameInput redistributable never surfaces Elite paddles** (the bridge now reports `hasFourPaddles: false`, activating the fallback chord layer) and does not enumerate Bluetooth Xbox controllers (XInput covers Bluetooth). Remaining for the exit gate: rumble-cue distinctness, reconnect behavior, and the formal per-transport `--validate` reports committed under `validation/`.

### Phase 2 — Managed GameInput adapter — **Complete in software, pending hardware sign-off**

Bridge client, `IControllerDevice`, serialized cancellable haptics, capability reporting, normalization/scheduling tests, defunct-bridge re-resolution.

### Phase 3 — Mapping engine — **Largely complete**

Delivered: versioned JSON profiles, press/release/tap/hold/double-press/axis-threshold gestures, chords, collision detection, enforced approval safeguards, import/export, capability-activated layers. Remaining: per-device match criteria beyond paddle capability, haptic cue overrides in profiles.

### Phase 4 — Codex adapter — **Implemented, pending live verification**

Owned app-server process, handshake, thread/turn lifecycle, approval correlation, interrupt/approve/decline commands, crash restart with backoff. Remaining: verification against a real Codex install; schema pinning; session navigation.

### Phase 5 — Windows desktop application — **Complete in software, pending visual verification**

Delivered (Avalonia): device/agent status, approval actions, prompt submission, haptic preview, event log, tray icon with hide-on-close, mapping editor with live validation and runtime apply, live controller mirror with approval highlights, CTRL·BOT shortcut coaching, settings persistence, the always-on-top overlay HUD, a first-run setup flow (no CLI flags or JSON needed), notification toasts when the windows are hidden (in-app cards with approve/decline on approval — deliberately not WinRT/Action Center toasts: zero dependencies, and the tray app is always running anyway), and a Validate-hardware button that launches the console wizard. **Exit gate items are all delivered**, plus the full UI-review polish pass (slim header + utility cluster, single action row, severity-tinted auto-scrolling event stream with a controller-input filter, floating approval banner, status dots, chord-chip binding rows, seamless extended title bar, entrance/pulse/blink animations, overlay parity), plus **Big Picture mode**: a Steam-style fullscreen controller-first UI (tile rail with focus ring, input capture with approval passthrough, offline voice prompts via System.Speech, fullscreen shortcuts screen, agent-response feed beside CTRL·BOT, bottom button legend, keyboard fallback). Remaining: a visual pass on real Windows — animations, DPI behavior of the extended title bar, first-render layout, and a microphone/dictation check for the voice flow.

### Phase 6 — Additional adapters — **Claude Code delivered (pulled forward)**

Target: all popular controllers × all popular agentic coding platforms. The launch pair is Xbox Elite + Codex; everything else plugs into the same two interfaces.

**Agent platform targets**

| Platform | Path | Status |
|---|---|---|
| Codex | app-server JSONL over stdio | Implemented, live verification pending |
| Claude Code | stream-json + `--permission-prompt-tool stdio` | **Verified live** (CLI 2.1.150, 2026-07-24): approval loop end to end; approve-for-session wire shape unit-tested but not yet exercised live |
| Cursor | `cursor-agent` CLI headless mode; JSON output exists — approval/permission wire protocol needs research | Planned |
| Google Antigravity | No public automation protocol known yet (VS Code-fork agent manager); watch for CLI/extension/MCP surface | Research |
| OpenCode / generic process adapters | stdio JSONL, same shape as existing adapters | Candidate |

**Controller targets**

| Controller | Path | Status |
|---|---|---|
| Xbox family | XInput (done) + GameInput bridge for trigger rumble (done); Elite paddles experimental — the PC redistributable never reports them (2026-07-24 evidence), pending a raw-report path | Supported |
| PS5 DualSense | Raw HID input reports + HID output for rumble/lightbar (community-documented format; no OS driver needed) | Implemented — hardware verification pending |
| DualSense Edge | Same HID path; rear paddles + Fn buttons map to the four paddle controls | Implemented — hardware verification pending |
| Generic pads | SDL2 or GameInput enumeration | Candidate |

Stream Deck / handheld / mobile frontends remain later-stage candidates.

## Release targets

- **v0.1.0 — Hardware proof:** tagged to exercise the release pipeline (self-contained win-x64 zip); the *hardware-proof* claim still needs the per-transport `--validate` reports committed under `validation/`. ← *current*
- **v0.2.0 — Mock two-way loop:** functionality complete today (profiles + physical feedback via mock agent); tag alongside v0.1 evidence.
- **v0.3.0 — Agent technical preview:** Codex and Claude Code verified against live installs; known protocol gaps closed.
- **v0.5.0 — Usable Windows preview:** tray app, mapping editor, starter profiles, packaged zip, validation wizard integrated in GUI.
- **v1.0.0 — Stable controller-agent interface:** documented extension contracts, dependable upgrades, tested transports, stable profile compatibility.
