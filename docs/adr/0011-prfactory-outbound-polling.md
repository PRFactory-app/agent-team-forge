# 0011. PRFactory connector: opt-in, outbound HTTPS polling

Status: accepted

## Context

PRFactory (app.prfactory.dev) lets people on a team create and assign tasks.
Its existing local worker already talks to the server by outbound HTTPS
polling (register, heartbeat, poll, claim, progress, artifacts, complete/fail),
not WebSocket or SignalR. ATF must keep working fully without it.

## Decision

- The connector lives inside the daemon and is off until `atf prfactory
  connect`. `atf prfactory disconnect` turns it off.
- It only makes outbound HTTPS calls with a repository-scoped PRFactory worker
  token. There is no inbound listener, tunnel or public MCP endpoint. The
  local IPC credential is never sent.
- A claimed work item becomes durable local state and ordinary ATF jobs.
  Submissions are keyed by server, work item, member and turn, so a retried
  claim never spawns twice.
- ATF owns accepted work. Losing the connection does not cancel jobs; uploads
  resume from persisted positions. Server-side, accepted ATF work must not be
  reassigned while the machine is offline (durable ATF acceptance).
- PRFactory receives persisted events; it never owns agent wake or execution.

## Consequences

- The server-side durable acceptance is a PRFactory change that must ship
  before end-to-end use against the hosted app.
- Details: [PRFactory connector](../prfactory-connector.md).
