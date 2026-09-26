# `stop_job` cannot stop a job in `needs_reconciliation` whose agent is still running

Found during Windows e2e testing on 2026-09-26. The logic lives in the platform-neutral
Business and DAL layers, so this is **not Windows-specific**.

## Symptoms

- A Codex job was in `needs_reconciliation` (`interactive_delivery_not_confirmed`, see the
  companion report), while its Codex TUI was alive and still working in its worktree.
- `atf client stop --job <id>` returned `{"ok":true,"outcome":"unchanged", ...}` and the agent
  kept running.
- The only way to stop it was to kill the process tree by hand (`taskkill /T /F` on the tab's
  PowerShell). That also bypasses atf's run bookkeeping.

## Where

- `src/AgentTeamForge.Business/Features/Jobs/StopJob.cs`: it calls `cancelRunning` only when
  `outcome.WasRunning`.
- `src/AgentTeamForge.DAL/Features/Jobs/JobStore.cs`, `Cancel(...)`: "Atomically cancels
  queued or running work; terminal jobs are unchanged." `needs_reconciliation` counts as
  terminal, so `Changed = false` and nothing is signalled.

## Why it matters

`needs_reconciliation` means atf is *uncertain*, not that the work has ended. The run can
still own a live process: a wt tab, a Herdr pane or a headless child. Right now, once a job
becomes uncertain, the user loses the documented way to stop it. `stop_job` promises to kill
the process tree. The MCP and web console give no alternative.

## Suggested direction

1. For a `needs_reconciliation` job whose last run has owned process evidence (`BackendPid`,
   the wt sidecar, or a Herdr pane binding), `stop` should do the following:
   - kill or close the owned process tree through the same backend-specific path used for
     running jobs (the wt tab owner, the Herdr pane, or the headless tree),
   - move the job to `cancelled` with reason `stopped`, and return `stopped`.
2. With no owned process evidence, keep today's `unchanged` result, but include a hint in the
   response (for example `reason: "no_owned_process"`) so the caller knows nothing was running.
3. Reuse the ownership checks already in `OrphanedBackendProcess` and `ReconcileStoppedJob`
   (see `FollowUpJob`), so that only processes atf provably owns are killed.
4. Tests: a `needs_reconciliation` job with a live fake backend PID must end with the process
   killed and the job `cancelled`. Without a PID, the result must be `unchanged` with a reason.
