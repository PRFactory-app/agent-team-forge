# 0010. Minimal web console on authenticated loopback

Status: accepted

## Context

The operator needs to see what agents are doing, send follow-ups and stop
jobs without going through a lead. A full dashboard or terminal emulator is out
of scope.

## Decision

- The daemon serves a small console that renders agent text as safe markdown
  and never HTML from agents on numeric loopback (`127.0.0.1`, default port
  8765) with static HTML, CSS and plain JavaScript files. No front-end framework or build step.
- Agent markdown is parsed by an own renderer into DOM nodes (no `innerHTML`,
  http(s) links only, no images).
- Every API call needs a random console bearer token, separate from the daemon
  IPC credential, stored owner-only in `web-console.key`. `atf web` prints a
  link with the token in the URL fragment; `--rotate-token` revokes old links.
- The server checks the exact `Host` header on every request and the exact
  `Origin` on writes. No cookies, no CORS, no credentials in query strings.
  Bodies and concurrent calls are bounded.
- The console calls the daemon through the same IPC operations as other
  clients. It never opens the database and never retries a mutation on its
  own.

## Consequences

- The console works in every launch mode. If its port is busy the daemon logs
  that and keeps serving jobs.
- It is not a remote-access feature; exposing it beyond loopback is
  unsupported.
- Details: [web console](../web-console.md).
