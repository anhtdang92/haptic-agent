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

`AgentEvent(AdapterId, SessionId, State, Timestamp, Message?, RequestId?, TurnId?, Model?)` with `AgentStateKind`:

| State | Meaning | Host reaction |
|---|---|---|
| `Idle` | nothing running | stops haptics; clears nothing by itself |
| `Working` | a turn is executing | working cue; **clears pending approval if the event carries a `RequestId`** |
| `ApprovalRequired` | agent wants permission; `RequestId` must be set | looping approval cue; arms approval bindings |
| `WaitingForInput` | agent asked a question; `RequestId` must be set | waiting cue; arms approval bindings |
| `Completed` | turn finished | completion cue; clears pending approval |
| `Error` | failure of any kind | error cue; clears pending approval |

The pending-approval lifecycle is the load-bearing part: publish `ApprovalRequired`/`WaitingForInput` with the request id, and make sure *some* later event (`Completed`, `Error`, or `Working`+`RequestId`) clears it — otherwise approval paddles stay armed forever.

`Model` is optional metadata: an adapter that knows which model is live (Claude Code states it in `system/init` every turn) stamps it on every event, and `HostEngine` folds it into `Settings` so UI model labels show the real model without the user touching a knob. Adapters that don't know leave it null — the label falls back to "default".

### Command semantics

`SubmitPrompt` (with `Text`), `Interrupt`, `ApproveOnce`, `ApproveForSession`, `Decline`, `Cancel` (approval-style when `RequestId` set, otherwise interrupt-style), `NewSession`, `NextSession`/`PreviousSession`, `ResumeSession` (`Text` carries the session/thread id — the command a UI session list issues when the user picks an entry directly, versus the blind cycle a controller button uses), `ReviewChanges`, `SetPermissionMode` (`Text` carries the adapter-defined mode name; adapters without modes publish an informational event instead of throwing). Approval commands arrive hydrated with the pending `SessionId`/`RequestId`.

## Mock adapter (`--agent mock`)

Simulates the full lifecycle (working → approval-required → completed/interrupted) without touching a real agent. Use it for end-to-end testing of mappings and haptics.

## Codex adapter (`--agent codex`)

Spawns `codex app-server --stdio` and speaks its JSON-RPC-style JSONL protocol.

- Inbound traffic is classified by the pure, unit-tested `CodexProtocolParser` (`CodexMessage` cases: `ThreadStarted`, `TurnStarted`, `TurnFinished`, `UserActionRequired`, `ServerRequestResolved`, `ResponseReceived`, `Ignored`). The parser reports what the wire said and leaves ids null when they are absent; the adapter owns the current thread/turn and fills the gaps, because only it knows what "current" means.
- Client requests used: `initialize`/`initialized` handshake, `thread/start` (with `approvalPolicy: "unlessTrusted"`), `thread/resume`, `turn/start`, `turn/interrupt`.
- Server requests (carry an `id`) are approval/input prompts → `ApprovalRequired` or `WaitingForInput` (for `item/tool/requestUserInput`); the raw JSON id is the `RequestId`, echoed back in the response with a decision of `accept`, `acceptForSession`, `decline`, or `cancel`.
- Notifications normalized: `thread/started` → Idle, `turn/started` → Working, `turn/completed` → Completed/Error/Idle by status (`failed` → Error, `interrupted` → **Idle** so a deliberate stop never fires the error cue, anything else → Completed), `serverRequest/resolved` → drops the pending entry.
- `SubmitPrompt` auto-creates a thread if none exists. `NewSession` starts an additional thread; `NextSession`/`PreviousSession` cycle the known thread list; `ResumeSession` targets a specific thread id. Switching always issues `thread/resume` for the target (harmless for live threads, and it reloads threads that only exist in Codex's on-disk rollout after a crash).
- After a crash restart, the adapter resumes the thread that was active when the server died (`thread/resume` from the on-disk rollout); if resume fails it reports why and the next prompt starts a fresh thread. The remembered thread list survives restarts.

## Claude Code adapter (`--agent claude`)

Spawns `claude --print --input-format stream-json --output-format stream-json --verbose --permission-prompt-tool stdio --include-partial-messages` in the working directory.

- Outbound: user turns as `{"type":"user","message":{role,content:[{type:"text",text}]}}`; interrupt as a `control_request` with subtype `interrupt`; runtime permission-mode switches as `control_request` subtype `set_permission_mode` (`mode`: `default`/`plan`/`acceptEdits`, wired to `AgentCommandKind.SetPermissionMode`); permission answers as `control_response` (`allow` echoes the tool input back as `updatedInput`; `deny` carries a message). Outbound wire shapes live in the pure `ClaudeControlRequest`/`ClaudePermissionResponse` builders (unit-tested).
- Inbound (classified by the pure, unit-tested `ClaudeStreamParser`): `system/init` → Idle (captures session id, **model, MCP server status, and the slash-command list** — surfaced in the ready message plus a `Commands: /compact /review …` line), `assistant` messages → Working (full text, or concrete tool detail — `Bash: npm test`, `Edit: src/App.cs`, and `TodoWrite` renders as live plan progress `Plan 2/5 — Fixing tests`), `user` messages carrying `tool_result` → Working `→ output snippet` (`→ ⚠ …` on error) so tool outcomes appear under each call, `result` → Completed or Error with turn stats appended (`(42.5s · 3 turns · $0.18)`), `control_request` subtype `can_use_tool` → ApprovalRequired rendered concretely (`Claude Code wants: Write: src/App.cs`; request id + tool input + any `permission_suggestions` stored), `control_cancel_request` → clears the pending approval.
- **Live streaming** (Claude-app parity): `stream_event` partial messages yield `TextDelta` (accumulated by the adapter and published as rolling Working snapshots at most every 250 ms, so UIs render the response as it is written) and `ThinkingStarted` → Working "Thinking…". `HostEngine` treats repeated Working events as UI-only: the event log records the state change once and the working rumble is not restarted per snapshot.
- `NewSession` restarts the CLI process (fresh session). The CLI runs one session per process, so `NextSession`/`PreviousSession` cycle the sessions this adapter has seen by restarting the process with `--resume <session-id>` — the target's history reloads from Claude Code's on-disk session store. `ResumeSession` restarts with a caller-supplied id the same way; the id may come straight from the on-disk store (it need not be one this adapter has seen — the CLI is the authority on whether it exists). A crash restart likewise resumes the session that was live when the process died.
- `ClaudeSessionCatalog` reads that on-disk store directly (`~/.claude/projects/<encoded-cwd>/<session-id>.jsonl`, where the encoded name replaces every character outside `[A-Za-z0-9]` with `-`): session id from the file name, title from a stored `summary` line or the first real user prompt (synthetic angle-bracket entries skipped), last activity from the file write time. No wire command lists past sessions, so the store is the only inventory; the format is observed (CLI 2.1.x), not documented, so parsing is tolerant and a title-less transcript still lists as "New session". This is what feeds the GUI's recents sidebar.
- `ApproveForSession` allows the request **and** adds permission rules so the tool stops prompting: when the CLI sent `permission_suggestions` they are echoed verbatim (exactly what the Claude app's "always allow" does); otherwise a session-scoped allow rule for the whole tool is synthesized (`addRules`/`allow`/`destination: session`). Payload shapes live in the pure `ClaudePermissionResponse` (unit-tested).
- **`system/init` arrives on every turn, not once per process** (verified 2026-07-26 against 2.1.220: three prompts produced three inits). The adapter announces a session only when the id actually changes, so the "session ready" banner and the slash-command list appear once rather than before every prompt. A relaunch (`NewSession`, session switch, crash restart) clears the cached id, so a genuinely new session still announces exactly once.
- Verified live against Claude Code CLI 2.1.150 (2026-07-23/24): `system/init` (session id), `assistant`, and `result` shapes match; the flag set including `--permission-prompt-tool stdio` is accepted, and the CLI also emits `system` subtypes the parser ignores (e.g. `api_retry`). The full approval loop ran on real hardware: a `can_use_tool` request for Write was **approved** via the RB+A chord (`allow` → file created with exact content) and, in a separate run, **declined** via RB+X (`deny` → Claude acknowledged and did not write). Ineligible approval chords pressed with no approval pending produced no command. **Full live sweep against 2.1.220 (2026-07-26)** — driven through the real adapter, 12 checks:
session init; a streaming prompt turn with turn stats; `ApproveOnce` (file written); `Decline` (file absent);
**`ApproveForSession`** (first Write prompted, the second did not — the session rule holds, the gap called out
above is now closed); `Interrupt` mid-turn reporting `Completed "Turn interrupted."`; all five permission modes
accepted with no error; `SetModel` echoing "Set model to Sonnet 5"; `CompactContext`; and crash restart
(SIGKILL → "exited unexpectedly with code 137; attempting restart").

**Session cycling remains unverified**, and cannot be verified from inside a Claude Code session: a nested CLI
reports the *outer* session's id for every session it starts, so `_sessionIds` collapses to a single entry and
`NextSession`/`PreviousSession` correctly report "no other session to switch to". Behavioural isolation does
work — a new session had no memory of a codeword taught to the previous one — but the id bookkeeping needs an
ordinary shell to exercise.

### Spawning agent CLIs on Windows

Bare names like `claude` or `codex` are resolved through `AgentExecutableResolver` (Core) before spawning: a shell-style PATH probe that honors PATHEXT, because npm installs provide only `.cmd`/`.ps1` shims and `CreateProcess` finds none of them by bare name. The resolver prefers launchable types (`.com`/`.exe`/`.bat`/`.cmd`) in PATHEXT order per directory, never selects `.ps1`, passes explicit paths through untouched, and probes the bare name as-is when PATHEXT is absent (non-Windows). `.cmd` shims spawn fine with redirected stdio once given their full path.

## Resilience pattern (Codex and Claude)

Both adapters implement the same recovery behavior — new adapters should too:

1. On unexpected child-process exit: fail all in-flight requests fast, clear pending approvals, publish an `Error` event (this also plays the error rumble).
2. Restart with capped exponential backoff (2 s → 15 s, max 5 attempts); publish Idle on success, a final Error on giving up. On success, resume the interrupted conversation where the platform allows it (Codex `thread/resume`, Claude `--resume`), falling back to a fresh session when resume fails.
3. While down, `ExecuteAsync` publishes an `Error` event and returns instead of throwing — host loops must keep running.
4. Guard against stale `Exited` handlers with a reference check on the current process, and suppress restart while intentionally replacing the process (e.g. `NewSession`).

## Writing a new adapter

1. New project `src/CtrlAgent.Adapters.<Name>` referencing **Core only**; add it to the solution and to the hosts' project references and `--agent` switch. The shared `HostEngine` covers the host loops, but each host still selects its own adapter, so update both switches: `Program.CreateAgentAdapter` in `CtrlAgent.App` and `App.CreateAgentAdapter` in `CtrlAgent.Gui`.
2. Keep protocol parsing in a pure, testable class (see `ClaudeStreamParser`); the process-management class should never touch raw JSON fields.
3. Publish events through an unbounded `Channel<AgentEvent>`; `ReadEventsAsync` just drains it. Never block the event stream on I/O.
4. Honor the pending-approval lifecycle and the resilience pattern above.
5. Add parser tests to `tests/CtrlAgent.Tests` (reference the adapter project) and document the protocol in this file.

## Planned adapters

- **Cursor** — `cursor-agent` has a headless CLI mode with JSON output, which fits the existing spawn-and-stream shape. Open questions before building: whether it emits a machine-readable event stream per turn, and whether tool approvals can be routed to the client (the approval loop is CtrlAgent's core value). Verify against an installed CLI first.
- **Google Antigravity** — no public automation protocol is known yet (it is a VS Code-fork with an agent manager). Watch for a CLI, extension API, or MCP surface; do not guess a wire format. Track in the roadmap as research.
- **OpenCode / generic process adapters** — stdio JSONL, same authoring checklist as above.

The Codex and Claude Code adapters are the reference implementations: spawn the platform's headless mode, keep parsing pure and unit-tested, normalize into `AgentEvent`s, honor the pending-approval lifecycle, restart with capped backoff.
