# MVP Claude backend review

Reviewed `74348ef` against `d7d24ae`, including the reference Claude Code backend in win-agent-teams. Verdict: **approve with fixes in this branch**.

Two runtime bugs were fixed:

- On this Linux host, the installed `claude` shim writes a mise status line to stdout before Claude's JSON. The backend rejected a successful real response as malformed. It now skips complete non-JSON prefix lines; a regression test covers this.
- An output larger than 4 MiB stopped reading but could leave the child blocked on stdout. Delivery failure could also leave a child running. The backend now terminates its owned process tree on those paths, and disposal terminates a child that remains after the exit wait. A regression test covers the output cap and child exit.

Validation with the repository .NET 11 SDK: root build passed; root tests passed (83 passed, one opt-in test skipped); `ATF_REAL_CLAUDE=1` passed the real prompt and native session resume test (one passed). The `Session` evidence is emitted by this backend; persistence and follow-up routing are outside this backend slice.
