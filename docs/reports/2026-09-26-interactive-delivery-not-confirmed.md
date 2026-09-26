# Interactive job marked `needs_reconciliation` although the agent received the prompt

Found during Windows e2e testing on 2026-09-26. The same code path exists for Herdr, so this is
**not Windows-specific**. Windows made it easy to reproduce because the VM was CPU-starved
(2 vCPU and 8 GB RAM, with three agents building .NET in parallel).

## Symptoms

- Four interactive Codex jobs in wt mode ended as `status: needs_reconciliation`,
  `reason_code: interactive_delivery_not_confirmed`: one submit and three follow-ups,
  two of them with `--interrupt`.
- In every case the agent **had** received the prompt and kept working. It wrote files, ran
  tests and committed in its worktree for another 15 to 30 minutes.
- Because the job is in a terminal state, atf lost track of a live agent:
  - `get_job` never shows the result.
  - Wake and completion notices are not delivered.
  - Follow-ups on the job fail with `parent_not_ready` (see `FollowUpJob`: `IsSessionFenced`).
  - `stop` does nothing to it (see the separate report on stopping `needs_reconciliation` jobs).

## Where

- `src/AgentTeamForge.Business/Features/Agents/Terminals/WtInteractiveBackend.cs`
  (`Run.DeliverAsync` / `ReadEvidenceAsync`, `interactive_delivery_not_confirmed`)
- `src/AgentTeamForge.Business/Features/Agents/Terminals/HerdrInteractiveBackend.cs`
  (same pattern, `_delivered`)
- `src/AgentTeamForge.Business/Features/Agents/Terminals/WtTabControl.cs`
  (`AwaitTabAsync`: fixed 12 s deadline for the tab to write its PID sidecar)

`DeliverAsync` sets `_launched = true` only if `StartAsync` returns normally. Any
`IOException`, timeout or `Win32Exception` leaves it `false`, and `ReadEvidenceAsync` then
immediately yields `interactive_delivery_not_confirmed`. The code comment even says a failed
launch is uncertain. Even so, the job is ended at once: nothing re-checks whether the tab or
agent actually started. A late sidecar, a transcript that later appears with the job's
`atf-corr:` marker, or a live owned process are all ignored.

## Likely trigger

Under heavy load, the wrapper takes longer than the 12 s `AwaitTabAsync` deadline to start
PowerShell, write the `.pid` sidecar and launch the agent. The launch actually succeeds
seconds later, but the run has already been ended. On Herdr, the equivalent is a slow or
failed `PromptAsync` that still reaches the pane.

## Suggested direction

1. Treat an uncertain launch as *pending confirmation*, not terminal. Keep polling for
   positive evidence for a bounded, generous window (for example 2 to 5 minutes), and
   confirm on whichever comes first:
   - the PID sidecar,
   - the backend's transcript containing the `atf-corr:<correlation>` marker,
   - the state hook reporting activity.
2. Only after that window, with no evidence, move to `needs_reconciliation`, and record the
   owned PID so that stop and reconciliation can act on it.
3. Make the 12 s tab deadline configurable or load-tolerant. Log how long the launch took
   when it succeeds after 5 s or more.
4. Add a test with a fake tab control whose sidecar appears after the deadline. The job must
   end `completed`, not `needs_reconciliation`.

## Repro (Windows)

1. Configure `atf setup --mode wt`.
2. Load the machine (for example, two parallel `dotnet build` runs on 2 vCPU).
3. `atf client submit --backend codex --model max --worktree ...`, then
   `atf client follow-up --interrupt ...` while it is working.
4. Observe `needs_reconciliation` / `interactive_delivery_not_confirmed` while the Codex tab
   keeps working.
