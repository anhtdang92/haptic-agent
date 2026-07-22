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
| 3 | Shared hosting layer (`CtrlAgent.Hosting`) | App and Gui run parallel copies of the same loops; divergence risk grows with every feature | Ready to build |
| 4 | Claude Code approve-for-session | Wire session-wide permission rules (`updatedPermissions`) instead of degrading to approve-once | Ready to build |
| 5 | GUI phase 2: tray icon, minimize-to-tray, mapping editor with live validation | Phase 5 exit gate needs profile editing without hand-written JSON | Ready to build |
| 6 | Release packaging: self-contained win-x64 zip (App + Gui + bridge) on tags | Install without a dev environment | Ready to build |
| 7 | Haptic pattern tuning pass | Depends on #1 evidence | Blocked on #1 |
| 8 | Session navigation (Codex threads, Claude `--resume`) | Next/PreviousSession are stubs in both adapters | Ready to design |
| 9 | Profile layers and per-device matching | Deferred from Phase 3 | Design first |
| 10 | OpenCode / generic process adapter | Phase 6 candidates | After #2 proves the pattern |

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

Delivered (Avalonia window): device/agent status, approval actions, prompt submission, haptic preview, active-profile display, event log. Remaining for the exit gate ("a new user needs no hand-edited JSON"): tray icon + notifications, mapping editor, validation-wizard integration, startup settings.

### Phase 6 — Additional adapters — **Claude Code delivered (pulled forward)**

Claude Code stream-json adapter with controller-answered permission prompts. Remaining candidates: OpenCode, generic process/webhook adapters, DualSense/SDL controllers, Stream Deck and mobile frontends — after the Elite + Codex/Claude path is verified stable.

## Release targets

- **v0.1.0 — Hardware proof:** software is ready (`--validate` is the inspector/test utility); tag once per-transport validation reports exist. ← *next release*
- **v0.2.0 — Mock two-way loop:** functionality complete today (profiles + physical feedback via mock agent); tag alongside v0.1 evidence.
- **v0.3.0 — Agent technical preview:** Codex and Claude Code verified against live installs; known protocol gaps closed.
- **v0.5.0 — Usable Windows preview:** tray app, mapping editor, starter profiles, packaged zip, validation wizard integrated in GUI.
- **v1.0.0 — Stable controller-agent interface:** documented extension contracts, dependable upgrades, tested transports, stable profile compatibility.
