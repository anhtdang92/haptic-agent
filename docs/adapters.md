# Agent Adapters

An agent adapter connects CtrlAgent to one AI coding agent. Adapters own their agent process, normalize its protocol into `AgentEvent`s, and execute `AgentCommand`s. Hosts never see raw protocol.

## The contract (`IAgentAdapter`)

```text
string Id                       stable adapter id ("mock", "codex", "claude")
bool   IsStarted
StartAsync(ct)                  spawn/connect; may publish an initial Idle event
ReadEventsAsync(ct)             async stream of AgentEvent (never ends until dispose)
ExecuteAsync(command, ct)       perform one AgentCommand
DisposeAsync()                  kill children, stop streams, complete the event channel
```

### Event semantics

`AgentEvent(AdapterId, SessionId, State, Timestamp, Message?, RequestId?, TurnId?)` with `AgentStateKind`:

| State | Meaning | Host reaction |
|---|---|---|
| `Idle` | nothing running | stops haptics; clears nothing by itself |
| `Working` | a turn is executing | working cue; **clears pending approval if the event carries a `RequestId`** |
| `ApprovalRequired` | agent wants permission; `RequestId` must be set | looping approval cue; arms approval bindings |
| `WaitingForInput` | agent asked a question; `RequestId` must be set | waiting cue; arms approval bindings |
| `Completed` | turn finished | completion cue; clears pending approval |
| `Error` | failure of any kind | error cue; clears pending approval |

The pending-approval lifecycle is the load-bearing part: publish `ApprovalRequired`/`WaitingForInput` with the request id, and make sure *some* later event (`Completed`, `Error`, or `Working`+`RequestId`) clears it — otherwise approval paddles stay armed forever.

### Command semantics

`SubmitPrompt` (with `Text`), `Interrupt`, `ApproveOnce`, `ApproveForSession`, `Decline`, `Cancel` (approval-style when `RequestId` set, otherwise interrupt-style), `NewSession`, `NextSession`/`PreviousSession`, `ReviewChanges`, `SetPermissionMode` (`Text` carries the adapter-defined mode name; adapters without modes publish an informational event instead of throwing). Approval commands arrive hydrated with the pending `SessionId`/`RequestId`.

## Mock adapter (`--agent mock`)

Simulates the full lifecycle (working → approval-required → completed/interrupted) without touching a real agent. Use it for end-to-end testing of mappings and haptics.

## Codex adapter (`--agent codex`)

Spawns `codex app-server --stdio` and speaks its JSON-RPC-style JSONL protocol.

- Client requests used: `initialize`/`initialized` handshake, `thread/start` (with `approvalPolicy: "unlessTrusted"`), `thread/resume`, `turn/start`, `turn/interrupt`.
- Server requests (carry an `id`) are approval/input prompts → `ApprovalRequired` or `WaitingForInput` (for `item/tool/requestUserInput`); the raw JSON id is the `RequestId`, echoed back in the response with a decision of `accept`, `acceptForSession`, `decline`, or `cancel`.
- Notifications normalized: `thread/started` → Idle, `turn/started` → Working, `turn/completed` → Completed/Error/Idle by status, `serverRequest/resolved` → drops the pending entry.
- `SubmitPrompt` auto-creates a thread if none exists. `NewSession` starts an additional thread; `NextSession`/`PreviousSession` cycle the known thread list. Switching always issues `thread/resume` for the target (harmless for live threads, and it reloads threads that only exist in Codex's on-disk rollout after a crash).
- After a crash restart, the adapter resumes the thread that was active when the server died (`thread/resume` from the on-disk rollout); if resume fails it reports why and the next prompt starts a fresh thread. The remembered thread list survives restarts.

## Claude Code adapter (`--agent claude`)

Spawns `claude --print --input-format stream-json --output-format stream-json --verbose --permission-prompt-tool stdio --include-partial-messages` in the working directory.

- Outbound: user turns as `{"type":"user","message":{role,content:[{type:"text",text}]}}`; interrupt as a `control_request` with subtype `interrupt`; runtime permission-mode switches as `control_request` subtype `set_permission_mode` (`mode`: `default`/`plan`/`acceptEdits`, wired to `AgentCommandKind.SetPermissionMode`); permission answers as `control_response` (`allow` echoes the tool input back as `updatedInput`; `deny` carries a message). Outbound wire shapes live in the pure `ClaudeControlRequest`/`ClaudePermissionResponse` builders (unit-tested).
- Inbound (classified by the pure, unit-tested `ClaudeStreamParser`): `system/init` → Idle (captures session id **and model**), `assistant` messages → Working (full text, or concrete tool detail — `Bash: npm test`, `Edit: src/App.cs`, and `TodoWrite` renders as live plan progress `Plan 2/5 — Fixing tests`), `result` → Completed or Error with turn stats appended (`(42.5s · 3 turns · $0.18)`), `control_request` subtype `can_use_tool` → ApprovalRequired (request id + tool name + input stored for the echo), `control_cancel_request` → clears the pending approval.
- **Live streaming** (Claude-app parity): `stream_event` partial messages yield `TextDelta` (accumulated by the adapter and published as rolling Working snapshots at most every 250 ms, so UIs render the response as it is written) and `ThinkingStarted` → Working "Thinking…". `HostEngine` treats repeated Working events as UI-only: the event log records the state change once and the working rumble is not restarted per snapshot.
- `NewSession` restarts the CLI process (fresh session). The CLI runs one session per process, so `NextSession`/`PreviousSession` cycle the sessions this adapter has seen by restarting the process with `--resume <session-id>` — the target's history reloads from Claude Code's on-disk session store. A crash restart likewise resumes the session that was live when the process died.
- `ApproveForSession` allows the request **and** adds a session-scoped allow rule for the whole tool (`updatedPermissions`: `addRules`/`allow`/`destination: session`), so that tool stops prompting for the rest of the session. Payload shapes live in the pure `ClaudePermissionResponse` (unit-tested).
- Verified live against Claude Code CLI 2.1.150 (2026-07-23/24): `system/init` (session id), `assistant`, and `result` shapes match; the flag set including `--permission-prompt-tool stdio` is accepted, and the CLI also emits `system` subtypes the parser ignores (e.g. `api_retry`). The full approval loop ran on real hardware: a `can_use_tool` request for Write was **approved** via the RB+A chord (`allow` → file created with exact content) and, in a separate run, **declined** via RB+X (`deny` → Claude acknowledged and did not write). Ineligible approval chords pressed with no approval pending produced no command. Only `ApproveForSession` has not been exercised against the live CLI (its wire shape is unit-tested).

### Spawning agent CLIs on Windows

Bare names like `claude` or `codex` are resolved through `AgentExecutableResolver` (Core) before spawning: a shell-style PATH probe that honors PATHEXT, because npm installs provide only `.cmd`/`.ps1` shims and `CreateProcess` finds none of them by bare name. The resolver prefers launchable types (`.com`/`.exe`/`.bat`/`.cmd`) in PATHEXT order per directory, never selects `.ps1`, passes explicit paths through untouched, and probes the bare name as-is when PATHEXT is absent (non-Windows). `.cmd` shims spawn fine with redirected stdio once given their full path.

## Resilience pattern (Codex and Claude)

Both adapters implement the same recovery behavior — new adapters should too:

1. On unexpected child-process exit: fail all in-flight requests fast, clear pending approvals, publish an `Error` event (this also plays the error rumble).
2. Restart with capped exponential backoff (2 s → 15 s, max 5 attempts); publish Idle on success, a final Error on giving up. On success, resume the interrupted conversation where the platform allows it (Codex `thread/resume`, Claude `--resume`), falling back to a fresh session when resume fails.
3. While down, `ExecuteAsync` publishes an `Error` event and returns instead of throwing — host loops must keep running.
4. Guard against stale `Exited` handlers with a reference check on the current process, and suppress restart while intentionally replacing the process (e.g. `NewSession`).

## Writing a new adapter

1. New project `src/CtrlAgent.Adapters.<Name>` referencing **Core only**; add it to the solution and to the hosts' project references and `--agent` switch (both `AppOptions`/`Program` and `GuiOptions`/`HostEngine` until the shared hosting layer exists).
2. Keep protocol parsing in a pure, testable class (see `ClaudeStreamParser`); the process-management class should never touch raw JSON fields.
3. Publish events through an unbounded `Channel<AgentEvent>`; `ReadEventsAsync` just drains it. Never block the event stream on I/O.
4. Honor the pending-approval lifecycle and the resilience pattern above.
5. Add parser tests to `tests/CtrlAgent.Tests` (reference the adapter project) and document the protocol in this file.

## Planned adapters

- **Cursor** — `cursor-agent` has a headless CLI mode with JSON output, which fits the existing spawn-and-stream shape. Open questions before building: whether it emits a machine-readable event stream per turn, and whether tool approvals can be routed to the client (the approval loop is CtrlAgent's core value). Verify against an installed CLI first.
- **Google Antigravity** — no public automation protocol is known yet (it is a VS Code-fork with an agent manager). Watch for a CLI, extension API, or MCP surface; do not guess a wire format. Track in the roadmap as research.
- **OpenCode / generic process adapters** — stdio JSONL, same authoring checklist as above.

The Codex and Claude Code adapters are the reference implementations: spawn the platform's headless mode, keep parsing pure and unit-tested, normalize into `AgentEvent`s, honor the pending-approval lifecycle, restart with capped backoff.
