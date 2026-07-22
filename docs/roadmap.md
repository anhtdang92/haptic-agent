# CtrlAgent Roadmap

The project advances through evidence-based gates. Each phase should produce something testable before the next layer is added.

## Phase 0 — Architecture and core contracts

Status: **Complete**

Deliverables:

- platform-independent controller and agent contracts;
- haptic frame and pattern model;
- built-in feedback cues;
- architecture blueprint;
- Xbox Elite validation plan;
- buildable mock feedback demo.

Exit gate:

- the solution builds on .NET 10;
- mock agent events route to deterministic haptic patterns;
- architecture review identifies no blocking issue.

## Phase 1 — Native GameInput hardware spike

Status: **Code complete — awaiting real Elite-controller validation**

Deliverables:

- minimal Windows C++ console project using Microsoft.GameInput 3.4;
- controller discovery and reconnect logging;
- state-change logging for standard controls and all four Elite paddles;
- four-motor rumble test menu;
- validation reports for USB, Xbox Wireless Adapter, and Bluetooth.

Exit gate:

- standard input is reliable over USB;
- paddle behavior is documented;
- two or more distinct rumble cues are reliable;
- no stuck rumble or stuck controls after reconnect.

## Phase 2 — Managed GameInput adapter

Status: **Complete in software — pending hardware sign-off**

Deliverables:

- narrow native bridge or generated managed interop;
- `IControllerDevice` implementation;
- serialized haptic playback with interruption priorities;
- device capability reporting;
- automated tests for event normalization and haptic scheduling.

Exit gate:

- managed demo receives live controller input;
- mock agent states trigger physical controller feedback;
- clean startup, cancellation, disconnect, reconnect, and shutdown.

## Phase 3 — Mapping engine

Status: **In progress** — versioned JSON profiles, press/release/tap/hold/double-press/axis-threshold gestures, collision detection, approval safeguards, and import/export are implemented; layers and modifier layers beyond chords remain.

Deliverables:

- versioned profile format;
- press, release, hold, double-press, chord, and axis-threshold gestures;
- layers and modifiers;
- collision detection;
- explicit safeguards for approval-capable commands;
- profile import and export.

Exit gate:

- a controller profile can map physical events to mock agent commands without keyboard injection;
- conflicting mappings are rejected or clearly surfaced;
- approval mappings require deliberate configuration.

## Phase 4 — Codex adapter

Status: **Implemented — crash recovery restarts the app-server with backoff; end-to-end verification against a real Codex install still pending**

Deliverables:

- owned local `codex app-server` process over stdio;
- initialization handshake;
- thread and turn lifecycle normalization;
- approval-request correlation by thread, turn, and request;
- commands for interrupt, approve, decline, new session, and session navigation;
- schema generation pinned to the installed Codex version where practical.

Exit gate:

- controller can safely respond to a real Codex approval request;
- completion, waiting, and error states produce physical cues;
- app-server crash and restart are recoverable.

## Phase 5 — Windows tray application

Deliverables:

- connected-device and agent status;
- mapping editor;
- live input inspector;
- haptic pattern preview;
- active-profile and active-session selection;
- compact HUD and Windows notifications;
- startup and update settings.

Exit gate:

- a new user can connect a controller, run hardware validation, select a safe starter profile, and use Codex without editing JSON manually.

## Phase 6 — Additional adapters

Candidates:

- Claude Code lifecycle hooks;
- OpenCode;
- generic process and webhook adapters;
- DualSense and generic SDL/GameInput controllers;
- Stream Deck, handheld, and mobile frontends.

These begin only after the Xbox Elite plus Codex path is stable.

## Release targets

### v0.1.0 — Hardware proof

Live Elite input inspector and haptic test utility.

### v0.2.0 — Mock two-way loop

Mapping engine plus physical feedback driven by mock agent events.

### v0.3.0 — Codex technical preview

Direct Codex app-server control and feedback without a polished editor.

### v0.5.0 — Usable Windows preview

Tray application, starter profiles, validation wizard, and safer approvals.

### v1.0.0 — Stable controller-agent interface

Documented extension contracts, dependable upgrades, tested transports, and stable profile compatibility.
