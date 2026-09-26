# Web console

The Linux operator console is a small text-only browser client for an existing AgentTeamForge daemon. Start it with `atf web --state-dir DIR --port PORT` and open the printed URL. Enter the printed token on the page. The process binds numeric `127.0.0.1` only; port `0` chooses a free port. It does not start the daemon or open its database.

The History table lists all jobs still retained by the daemon (the default prune window is 30 days), with status filters and cursor paging. Rows show backend, session, parent, status and attempts. Select a job to see its result and incrementally tailed output. The console polls while a job is queued or running and stops polling after a terminal state. A job with a session ID has a Follow up button; this sends a new instruction through the daemon's `job_follow_up` operation. On a running job, check **Interrupt running job** to cancel the active attempt and enqueue the new instruction. Stop asks for confirmation and reports the daemon's outcome, status and reason code.

A follow-up uses one idempotency key for the attempt. If the outcome is uncertain, the page offers an explicit retry with that same key or discard. The token stays in page memory; reload requires entering it again. API calls require the bearer, exact loopback Host, and exact Origin on POST. The page renders response text as text, and no credentials are placed in URLs or browser storage.

The console uses the daemon's `job_list`, `job_get`, `job_output`, `job_follow_up`, and `job_stop` IPC operations. Its list is a live cursor scan, so jobs whose status changes during paging can move between filtered pages. Output offsets are absolute bytes; if old bytes have been trimmed, the page marks the gap and resumes at the retained start.
