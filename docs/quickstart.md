# Linux quickstart

## Publish and set up

From the repository root, run `DOTNET=.tools/dotnet11/dotnet scripts/verify.sh`.
It prints the path to a tested Linux binary. Use that path for `ATF` below.
Install and sign in to the Claude Code and Codex CLIs before submitting real jobs.

```bash
ATF=/absolute/path/to/published/atf
STATE="$HOME/.local/state/agentteamforge"
"$ATF" setup --mode headless --state-dir "$STATE" --apply
"$ATF" start --state-dir "$STATE"
```

`--apply` registers the ATF MCP server in the current user's Claude Code and
Codex configurations. Omit it to print the registration commands without
changing those configurations. For visible interactive agent TUIs, choose
`--mode herdr` at setup instead; it requires Herdr on Linux. Keep the same
`--state-dir` for setup, start, MCP registration, clients, and stop.

## Lead and jobs

A Codex lead opened directly in a new repository may show **Trust this folder**
once, even in permission bypass mode. Accept that prompt before leaving the
lead unattended. In a live check, an ATF-managed Herdr Codex agent started and
completed in a fresh job worktree without operator input.

In a Codex lead, read `CODEX_THREAD_ID` with a shell tool and call
`register_codex_wake(thread_id)` before submitting jobs. The MCP bridge does
not always inherit that variable. For a Claude Code lead in bypass mode,
`setup --apply` enables incoming notices for the user's Claude configuration.

Use the MCP tools `submit_job`, `get_job`, `get_job_output`, `follow_up`,
`stop_job`, and `list_jobs`. For example, submit two independent jobs with
different `idempotency_key` values, `backend="codex"` or `"claude"`,
`cwd` set to a Git repository, and `worktree=true`. Each job gets a private
worktree. When native wake says work is ready, call `list_jobs` and `get_job`;
the wake is a notice, while the job record holds the result. A follow-up
resumes the parent's native session and worktree.

The CLI uses the same daemon IPC:

```bash
"$ATF" client submit --state-dir "$STATE" --backend codex --cwd "$PWD" \
  --worktree --key example-1 --instruction 'Reply OK.'
"$ATF" client get --state-dir "$STATE" --job JOB_ID
"$ATF" client logs JOB_ID --follow --state-dir "$STATE"
"$ATF" client list --state-dir "$STATE"
"$ATF" stop --state-dir "$STATE"
```
