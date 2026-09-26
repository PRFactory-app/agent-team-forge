# Staffing documentation review

**Verdict: CHANGES-REQUIRED.** Reviewed Claude-authored `9641ce3` against `efcb374` and the owner's staffing decision. The diff is English, concise, and preserves the product vision, architecture, TDD, security, and opposite-family code-review rules. `git diff --check efcb374 9641ce3` passes. Documentation-only review; no runtime gates apply.

## Blocking findings

1. **Worker capacity and orchestrator role.** `AGENTS.md:98–102` and `docs/claude-orchestrator-handoff.md:10–17` set a six-to-eight-worker target, while the owner authorized up to **14** parallel agents. State that the Claude Code orchestrator does no hands-on work at all; the current wording excludes only implementation and review. Update the active handoff's capacity language consistently.
2. **Pi delegation.** `AGENTS.md:103–107` says all workers use win-agent-teams and forbids native subagents without qualifying the owner's explicit allowance for a Pi planner to use its own subagents or act as a sub-team lead. Distinguish orchestrator-spawned workers from Pi-internal delegation and retain clear ownership/reporting of that work.
3. **Conflicting live instructions.** The linked `docs/spikes/product-core-consolidation-plan.md:185` still routes new Pi/Codex workers through native subagents; `docs/implementation-status.md:70–73` repeats that route and a five-to-six-lane target. `HANDOFF.md:21–27` still describes the current priority as roughly six lanes with Pi planning at medium effort, and `AGENTS.md:127` still lists Claude Opus as a normal plan author. Align active instructions with Pi tier max and the new route, or explicitly label the old assignments as historical snapshots so they cannot govern new dispatch.

Re-review the corrected diff before treating this staffing change as accepted.
