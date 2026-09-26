# Independent review: product core consolidation plan

**Plan reviewed:** `bf4ffc1b5392afe8de878b66b897d3b6521623ae`,
`docs/spikes/product-core-consolidation-plan.md`.
**Current promotion evidence read:** `765fe1df5a04eb232cf8f1706085440de39f93f6`,
`docs/spikes/m0-promotion-check.md`; architecture and contributor instructions
were also read. **Verdict: approved for the bounded canonical lift, with the
acceptance conditions below.** This approves a plan, not source, tests, a merge,
publication, or a product phase exit. The already reviewed fake checkpoint at
`81a11b27fb67d2ec600ace67586c092829927f8f` remains available independently.

## Assessment

The proposed root `AgentTeamForge.slnx`, three production projects (Host,
Business, DAL), and one test project are the smallest maintainable structure for
this lift. Keeping the existing feature slices, fake-only profile, schema,
policy, and protocol intact avoids turning a path change into a new engine
design. The explicit blob allowlist, source mapping, separate review, and
file-by-file retirement gates address the main risk of importing legacy branch
ancestry or deleting unique evidence. There is no reason to introduce
repository ports, a fourth production project, or a schema change for this
refactor.

The newer inspection integration is **not a green promotion input**. The
`765fe1d` check records 73 passed and 1 failed of 74 full tests, twice, from a
timing-dependent ordering assertion in `ListJobsTests`; separately rerun AOT,
20 published scenarios, and the demo passed. Therefore `verify.sh` failed on
that snapshot. A fresh writer's pending fix is not evidence until the exact fix
has independent rereview and the combined full gate is green.

## Conditions for implementation and acceptance

1. **Freeze the input and provenance.** Select either the reviewed `81a11b2`
   fake-core source or a later immutable inspection commit that has an
   independently reviewed deterministic test fix and green combined gates.
   Record its tree and per-file blob IDs and modes, source reviews, exclusions,
   and destination mapping. Import only allowlisted tracked blobs and cleared
   public evidence. Compare the extracted tree with that source before path
   edits, then inspect the staged tree and newly reachable ancestry. No
   whole-branch merge, inherited legacy parent, untracked files, ignored
   artifacts, credentials, or raw evidence may enter the candidate.
2. **Isolate root build settings from retained legacy.** Moving `global.json`,
   `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`, and
   `.editorconfig` to the root changes their discovery scope. In particular,
   the root SDK pin and NuGet configuration may affect commands run against the
   retained .NET 10 interactive tree, even though that tree has its own
   `Directory.Build.props`. Record before/after effective SDK, target framework,
   package version and feed selection, analyzer/AOT properties, and the legacy
   command working directory. If any effective input changes, add the smallest
   explicit isolation and rerun affected checks. Do not silently retarget or
   reinterpret the earlier .NET 10 evidence as .NET 11 evidence.
3. **Dispose of the Host test hook explicitly.** The current `JobsEndpoint`
   invokes DAL `DurabilityCheckpoints.AcceptAfterCommit` after Business accepts
   a job. Before the product-path move, either route that fake-profile checkpoint
   through a small Business-owned seam, or record a narrowly scoped temporary
   exception for this after-commit test hook with its removal trigger and proof
   that normal job operations still use Business and bridge/client modes do not
   open the database. Keep the checkpoint's failure timing and regression
   coverage. This is a bounded Host endpoint issue, not a reason to change the
   three-project dependency direction or add Clean Architecture interfaces.
4. **Prove the relocated candidate.** Review the actual import, path, config,
   script, and test diff with the opposite model family; rereview fixes. From a
   fresh output tree, run the pinned SDK restore, format, warning-clean Release
   build, full tests, Linux x64 AOT publish, published process scenarios, and
   exactly one published-binary demo scenario. Check solution references, Host
   apphost/native asset copying, test discovery, executable script modes,
   shell syntax, current documentation links and commands, ignored state paths,
   and diff whitespace. Record selected-source test counts and binary hash;
   earlier 61/19 or later 74/20 counts are evidence for their own snapshots,
   not expected results for a different candidate.
5. **Retire only proven duplicates.** Keep the separate interactive tree and its
   unique protocol, ownership, recovery, and live-session evidence until a
   snapshot-bound manifest records each file's reviewed destination or explicit
   non-promotion decision, replacement regression, and live dependency. Remove
   duplicate durable-core runtime files only after the canonical replacement
   passes review and combined gates. Do not clean untracked recovery state or
   stop processes as part of a source lift.

Local implementation and review on feature branches are already authorized for
this repository; they need no new public-permission prerequisite. Advancement
to the milestone branch follows the existing reviewed integration gate.
Publication and its license/distribution decisions remain separate. A green
Linux fake-core lift does not qualify real agents, interactive terminals,
native wake, Windows/macOS, or full product scope.

This was a documentation and read-only source review. No runtime command,
source movement, code change, merge, or publication was performed.
