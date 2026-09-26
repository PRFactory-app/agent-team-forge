# Canonical promotion docs review

Reviewer: Claude Opus (opposite family to the GPT-6 Astra author). Reviewed
`0cd4290` (README.md, HANDOFF.md §1, docs/implementation-status.md), rebased
onto `main` `d7d24ae`.

**Verdict: APPROVED with fixes applied.**

- Commands, `DOTNET` override, `ATF_DEMO_BIN` and git-common-dir SDK discovery
  match `scripts/demo.sh`/`verify.sh`. No real-agent, platform or native-wake
  over-claims found.
- Fixed stale state: counts updated from 61/19/1 at `2d6d0c9` to 76/76 tests,
  22/22 published scenarios and 1/1 demo at `main` `d7d24ae` (integration tip
  `e2340dd`), with the intermittent 21/22 first run disclosed; "promotion
  pending" replaced by promoted; P2/D2 slices mentioned; next gate now the flake.
- HANDOFF §1 conflict resolved by keeping the concise new text and restating the
  `AGENTS.md` staffing rules (win-agent-teams, up to 14, Pi plans / Opus codes /
  Codex reviews and integrates).
- Non-blocking: `published-smoke.sh` falls back to PATH when no SDK is found,
  which the docs do not mention.
