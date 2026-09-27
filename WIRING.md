# Slice 6: publication core integration

This branch deliberately does not edit shared orchestration, DTOs, schema registration,
or slice 5 files. It does not create PRs: PRFactory's existing provider flow owns that.
No capability advertisement is justified until these integration steps and server work land.

## Composition and migration

Register `PRFactoryPublicationMigration.Sql` in the next numbered `Schema` migration
(the integrator assigns the number). Tests explicitly apply this unregistered migration.
Construct one `PRFactoryPublicationStore(database)` and one `BranchPublisher` per connector;
dispose the publisher at connector shutdown. The daemon remains the single writer.

Pass slice 4's `PRFactoryAuthority.RunAsync` as `PublicationAuthority`:

```csharp
new BranchPublisher(publications,
    (id, effect, ct) => authority.RunAsync(id, effect, ct));
```

`RunAsync` must have freshly confirmed accepted server disposition; after restart or
transport loss it must refuse until reconfirmed. This is an effect admission hook, not a
boolean checked earlier in FinishAsync. A push already admitted cannot be revoked.
Publisher verifies and stores its receipt even if fencing occurs during that push;
server uploads/completion must independently pass authority again. Never wrap the whole
publisher in the authority gate instead of guarding the actual push.

## Canonical workspace projection (slice 5)

Use the persisted `WorkspaceSnapshot` from `PRFactoryWorkspaceStore.Get`, as defined on
`feat/prf-team-workspace`. Do not prepare another checkout. Require all children integrated,
all writers stopped, and the lead finalization turn complete before freezing/publishing.
Maintain that quiescence until completion. Map these exact snapshot properties:

| PublicationRequest | WorkspaceSnapshot |
| --- | --- |
| WorkspaceKey | Key |
| RepositoryId | RepositoryId (non-null) |
| LeadPath | LeadPath |
| Remote | Remote (validated mapping, non-null) |
| InternalBranch | InternalBranch |
| PublishBranch | PublishBranch (resolved claim/ProjectInit branch) |
| BaseSha | BaseSha |
| ReadOnly | ReadOnly, also enforce claim/repository write authorization |

Populate Server, WorkItemId, LeaseToken, MachineId and JobId from durable accepted team
identity. PublicationId must be a persisted stable unique key for server + accepted
work item/phase attempt + repository (not a newly generated ID on retry). Type and
ProjectInit come from the claim. Skip scratch workspaces before building a request;
never invent a repository identity for them. Read-only and non-publication phases return
null and must produce Skipped repository outcomes, not fabricated Pushed receipts.
For multi-repo integration construct one request per authorized writable repository.
Freeze all intended heads before the first remote mutation in slice 8; this core handles
one repository at a time, retaining partial receipts if later repositories fail.

## Exact orchestration call sites

1. `PRFactoryWorkItems.FinishAsync`: after canonical lead validation/finalization and before
   `teams.FreezeArtefacts`, call `PublishAsync` for successful writable publication phases.
   Ensure collector output is staged outside the checkout (slice 5 `StagingPath`), or
   committed by the agent before this point. No implicit untracked artefact exclusion.
   Also call it on recovery when artefacts are already frozen/uploaded: receipts are
   revalidated against the live remote before any success completion is replayed.
2. Publish failures must prevent successful completion. Dirty output reports porcelain
   paths without auto-committing. Network/push uncertainty retains the same intent for retry;
   never replace its key or frozen SHA. Local files and committed output remain intact.
   An acknowledged receipt whose remote SHA changed requires reconciliation.
3. Before the existing `client.CompleteAsync` in `FinishAsync`, report each verified
   repository outcome using PRFactory's existing repository-result endpoint under authority.
   Reuse the same payload after response loss. Send `PushState=Pushed`, repository identity,
   PublishBranch and HeadSha; add PublicationId, remote ref (`refs/heads/` + PublishBranch),
   BaseSha and VerifiedAt evidence to the negotiated receipt wire contract. A skipped phase
   must not send a made-up publication receipt. This slice adds no competing wire DTO.
4. Replace current `JobWorktree.Branch(cwd)` / `JobWorktree.Head(cwd)` completion arguments
   with the verified receipt's `Intent.PublishBranch` / `Intent.HeadSha`. The internal
   `atf/team/...` branch is never the public result branch. Gate completion via authority
   independently, preserving already-pushed evidence when completion is fenced.
5. Pin workspace, intent and receipt during pending reports/completion/reconciliation.
   Do not prune based solely on the agent job's terminal state. Server provider work must
   verify remote branch/SHA and reconcile existing matching PR after response loss.

## Policy and evidence

Read-only inspection of PRFactory `WorkItemExecutor.CompleteSingleRepoAsync`,
`CompleteMultiRepoAsync`, `PublishesBranch`, `GitWorktreeManager.PushBranchAsync`, and
`WipBranchPublisher` shows committed-tip push, no host blanket commit, normal push without
force, lease check before push and remote reconciliation after lost responses. The current
single-repo `PublishesBranch` only lists Implementation/CodeReview plus ProjectInit;
this slice also supports writable CustomStep as explicitly required by the replacement plan.
Dirty-tree rejection is intentionally stricter than that worker's single-repo path.

Git runs with the user's ordinary credential helper/SSH environment; ATF does not supply
tokens. Fetch and push origin URLs must both exactly match the validated mapping; multiple
URLs are rejected. Local targets must be bare. Push uses the frozen internal branch tip
SHA as source and requested public ref as destination, without force or follow-tags.
`ExpectedRemoteSha` records the initial remote observation for future drift policy, not
permission to force. `ls-remote` after push must equal the frozen head before a receipt is
saved. Later remote changes remain possible; PRFactory must verify again for PR creation.

Tests use only disposable local bare remotes and injected durability boundaries. They do
not prove provider PR creation or live connector integration. Opposite-family review is
left to the lead (this assignment explicitly prohibits sub-agents).
