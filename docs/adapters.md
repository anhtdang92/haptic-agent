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

`SubmitPrompt` (with `Text`), `Interrupt`, `ApproveOnce`, `ApproveForSession`, `Decline`, `Cancel` (approval-style when `RequestId` set, otherwise interrupt-style), `NewSession`, `NextSession`/`PreviousSession`, `ReviewChanges`. Approval commands arrive hydrated with the pending `SessionId`/`RequestId`.

## Mock adapter (`--agent mock`)

Simulates the full lifecycle (working → approval-required → completed/interrupted) without touching a real agent. Use it for end-to-end testing of mappings and haptics.

## Codex adapter (`--agent codex`)

Spawns `codex app-server --stdio` and speaks its JSON-RPC-style JSONL protocol.

- Client requests used: `initialize`/`initialized` handshake, `thread/start` (with `approvalPolicy: "unlessTrusted"`), `turn/start`, `turn/interrupt`.
- Server requests (carry an `id`) are approval/input prompts → `ApprovalRequired` or `WaitingForInput` (for `item/tool/requestUserInput`); the raw JSON id is the `RequestId`, echoed back in the response with a decision of `accept`, `acceptForSession`, `decline`, or `cancel`.
- Notifications normalized: `thread/started` → Idle, `turn/started` → Working, `turn/completed` → Completed/Error/Idle by status, `serverRequest/resolved` → drops the pending entry.
- `SubmitPrompt` auto-creates a thread if none exists. `NewSession` starts an additional thread; `NextSession`/`PreviousSession` cycle the known thread list (the active thread receives subsequent prompts/interrupts). The thread list is cleared on app-server crash — resuming threads across restarts (`thread/resume`) is not wired yet.

## Claude Code adapter (`--agent claude`)

Spawns `claude --print --input-format stream-json --output-format stream-json --verbose --permission-prompt-tool stdio` in the working directory.

- Outbound: user turns as `{"type":"user","message":{role,content:[{type:"text",text}]}}`; interrupt as a `control_request` with subtype `interrupt`; permission answers as `control_response` (`allow` echoes the tool input back as `updatedInput`; `deny` carries a message).
- Inbound (classified by the pure, unit-tested `ClaudeStreamParser`): `system/init` → Idle (captures session id), `assistant` messages → Working (text snippet or "Using tool: X"), `result` → Completed or Error, `control_request` subtype `can_use_tool` → ApprovalRequired (request id + tool name + input stored for the echo), `control_cancel_request` → clears the pending approval.
- `NewSession` restarts the CLI process (fresh session). `NextSession`/`PreviousSession` report that Claude Code runs one session per process — multi-session needs a `--resume` design first.
- `ApproveForSession` allows the request **and** adds a session-scoped allow rule for the whole tool (`updatedPermissions`: `addRules`/`allow`/`destination: session`), so that tool stops prompting for the rest of the session. Payload shapes live in the pure `ClaudePermissionResponse` (unit-tested).
- Known limit: the wire shapes follow the Agent SDK protocol and still need verification against an installed CLI.

## Resilience pattern (Codex and Claude)

Both adapters implement the same recovery behavior — new adapters should too:

1. On unexpected child-process exit: fail all in-flight requests fast, clear pending approvals, publish an `Error` event (this also plays the error rumble).
2. Restart with capped exponential backoff (2 s → 15 s, max 5 attempts); publish Idle on success, a final Error on giving up.
3. While down, `ExecuteAsync` publishes an `Error` event and returns instead of throwing — host loops must keep running.
4. Guard against stale `Exited` handlers with a reference check on the current process, and suppress restart while intentionally replacing the process (e.g. `NewSession`).

## Writing a new adapter

1. New project `src/CtrlAgent.Adapters.<Name>` referencing **Core only**; add it to the solution and to the hosts' project references and `--agent` switch (both `AppOptions`/`Program` and `GuiOptions`/`HostEngine` until the shared hosting layer exists).
2. Keep protocol parsing in a pure, testable class (see `ClaudeStreamParser`); the process-management class should never touch raw JSON fields.
3. Publish events through an unbounded `Channel<AgentEvent>`; `ReadEventsAsync` just drains it. Never block the event stream on I/O.
4. Honor the pending-approval lifecycle and the resilience pattern above.
5. Add parser tests to `tests/CtrlAgent.Tests` (reference the adapter project) and document the protocol in this file.

Candidates on the roadmap: OpenCode, generic process/webhook adapters.
