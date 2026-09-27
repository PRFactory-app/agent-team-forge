using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class AttachmentTests
{
    static PRFactoryWorkItem Item(bool repository = true) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repository ? Guid.NewGuid() : null,
        Type = "Implementation",
        AgentType = PRFactoryAgentType.Codex,
        LeaseToken = Guid.NewGuid(),
        AttemptCount = 2,
        TicketArtefactFolder = "ticket",
        Prompt = "work"
    };

    static string Attachment(JobRecord job, string name, byte[] bytes)
    {
        var folder = Directory.CreateDirectory(Path.Combine(job.Cwd!, "ticket", "attachments")).FullName;
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Published_diff_and_binary_use_exact_multipart_contract_before_completion()
    {
        using var h = new ChainHarness(Item());
        h.Server.BlobsSupported = true;
        await h.TickAsync();
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 255 };
        var job = Assert.Single(h.RunQueued(j =>
        {
            ChainHarness.Commit(j.Cwd!, "feature.txt", "hello\n");
            Attachment(j, "screen.png", png);
        }));
        var head = ChainHarness.Git(job.Cwd!, "rev-parse", "HEAD");
        await h.TickAsync();
        Assert.Empty(h.Server.Failures);
        Assert.Equal(2, h.Server.Blobs.Count);
        var diff = Assert.Single(h.Server.Blobs, b => b.Fields["Kind"] == "diff");
        Assert.Equal("changes.patch", diff.Fields["FileName"]);
        Assert.Equal("text/x-diff", diff.Fields["MediaType"]);
        Assert.Equal(h.BaseSha, diff.Fields["BaseCommitSha"]);
        Assert.Equal(head, diff.Fields["HeadCommitSha"]);
        Assert.Equal("false", diff.Fields["IsTruncated"]);
        Assert.Contains("+hello", Encoding.UTF8.GetString(diff.File));
        Assert.Equal(ChainHarness.Git(job.Cwd!, "diff", h.BaseSha + "..." + head), Encoding.UTF8.GetString(diff.File).Trim());
        var image = Assert.Single(h.Server.Blobs, b => b.Fields["Kind"] == "attachment");
        Assert.Equal("screen.png", image.Fields["FileName"]);
        Assert.Equal("image/png", image.Fields["MediaType"]);
        Assert.Equal(png, image.File);
        Assert.False(image.Fields.ContainsKey("BaseCommitSha"));
        Assert.Equal(["artefacts", "blob", "blob", "complete"], h.Server.UploadOrder);
        Assert.Single(h.Server.Completions);
    }

    [Fact]
    public async Task Lost_response_replays_frozen_bytes_and_key_after_reopening_stores_and_file_edits()
    {
        using var h = new ChainHarness(Item(false)) { AllowRepoLess = true };
        h.Server.BlobsSupported = true;
        h.Server.LoseBlobResponse = true;
        await h.TickAsync();
        var path = "";
        h.RunQueued(j => path = Attachment(j, "report.txt", "original"u8.ToArray()));
        await h.TickAsync();
        Assert.Single(h.Server.Blobs);
        Assert.Empty(h.Server.Completions);
        var reopened = new PRFactoryTeamStore(h.Database);
        var pending = Assert.Single(reopened.PendingAttachments(ChainServer.Url, h.Server.Item.Id)!);
        Assert.Equal(h.Server.Blobs[0].Payload, pending.Payload);
        h.Server.BlobsSupported = false;
        await h.TickAsync();
        Assert.Empty(h.Server.Completions);
        Assert.Single(h.Server.Blobs);
        h.Server.BlobsSupported = true;
        File.WriteAllText(path, "changed after response loss");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "new.txt"), "must not extend frozen batch");
        await h.TickAsync(); // A new adapter/client is constructed on every tick.
        Assert.Equal(2, h.Server.Blobs.Count);
        Assert.Equal(h.Server.Blobs[0].Payload, h.Server.Blobs[1].Payload);
        Assert.Equal(h.Server.Blobs[0].Fields["ClientKey"], h.Server.Blobs[1].Fields["ClientKey"]);
        Assert.Empty(reopened.PendingAttachments(ChainServer.Url, h.Server.Item.Id)!);
        Assert.Single(h.Server.Completions);
    }

    [Fact]
    public async Task Capability_absent_skips_blob_collection_and_upload()
    {
        using var h = new ChainHarness(Item(false)) { AllowRepoLess = true };
        await h.TickAsync();
        h.RunQueued(j => Attachment(j, "oversized.txt", new byte[PRFactoryAttachments.MaxFileBytes + 1]));
        await h.TickAsync();
        Assert.Empty(h.Server.Blobs);
        Assert.Null(h.Teams.PendingAttachments(ChainServer.Url, h.Server.Item.Id));
        Assert.Single(h.Server.Completions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversize_file_or_attempt_fails_before_any_blob_is_sent(bool attempt)
    {
        using var h = new ChainHarness(Item(false)) { AllowRepoLess = true };
        h.Server.BlobsSupported = true;
        await h.TickAsync();
        h.RunQueued(j =>
        {
            if (attempt)
            {
                var bytes = Enumerable.Repeat((byte)'a', PRFactoryAttachments.MaxFileBytes).ToArray();
                for (var i = 0; i < 6; i++) { Attachment(j, i + ".txt", bytes); }
            }
            else { Attachment(j, "large.pdf", new byte[PRFactoryAttachments.MaxFileBytes + 1]); }
        });
        await h.TickAsync();
        Assert.Contains("limit exceeded", Assert.Single(h.Server.Failures));
        Assert.Empty(h.Server.Blobs);
        Assert.Empty(h.Server.Completions);
    }

    [Fact]
    public async Task Large_diff_is_capped_with_frozen_sha_metadata_and_binary_summary()
    {
        using var h = new ChainHarness(Item());
        h.Server.BlobsSupported = true;
        await h.TickAsync();
        h.RunQueued(j =>
        {
            File.WriteAllBytes(Path.Combine(j.Cwd!, "binary.dat"), [0, 255, 0, 42]);
            ChainHarness.Git(j.Cwd!, "add", "binary.dat");
            ChainHarness.Commit(j.Cwd!, "large.txt", new string('x', PRFactoryAttachments.MaxFileBytes + 100));
        });
        await h.TickAsync();
        var blob = Assert.Single(h.Server.Blobs);
        Assert.Equal(PRFactoryAttachments.MaxFileBytes, blob.File.Length);
        Assert.Equal("true", blob.Fields["IsTruncated"]);
        Assert.Contains("binary.dat", blob.Fields["BinaryChangeSummary"]);
        Assert.Single(h.Server.Completions);
    }

    [Fact]
    public async Task Non_utf8_source_diff_is_sent_as_valid_utf8_text()
    {
        using var h = new ChainHarness(Item());
        h.Server.BlobsSupported = true;
        await h.TickAsync();
        h.RunQueued(j =>
        {
            File.WriteAllBytes(Path.Combine(j.Cwd!, "legacy.txt"), [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n']); // Latin-1 "café"
            ChainHarness.Git(j.Cwd!, "add", "legacy.txt");
            ChainHarness.Commit(j.Cwd!, "feature.txt", "hello\n");
        });
        await h.TickAsync();
        var blob = Assert.Single(h.Server.Blobs);
        var text = new UTF8Encoding(false, true).GetString(blob.File);
        Assert.Contains("+caf\uFFFD", text);
        Assert.Contains("Non-UTF-8", blob.Fields["BinaryChangeSummary"]);
        Assert.Single(h.Server.Completions);
    }

    [Fact]
    public async Task Conflict_is_a_persisted_failure_diagnostic()
    {
        using var h = new ChainHarness(Item(false)) { AllowRepoLess = true };
        h.Server.BlobsSupported = true;
        h.Server.BlobRejection = HttpStatusCode.Conflict;
        await h.TickAsync();
        h.RunQueued(j => Attachment(j, "report.txt", "report"u8.ToArray()));
        await h.TickAsync();
        Assert.Contains("HTTP 409", Assert.Single(h.Server.Failures));
        Assert.Contains("HTTP 409", h.Teams.ArtefactDelivery(ChainServer.Url, h.Server.Item.Id)!.Failure!);
        Assert.Empty(h.Server.Completions);
    }

    [Fact]
    public async Task Revoked_authority_between_uploads_blocks_remaining_blobs_and_completion()
    {
        using var h = new ChainHarness(Item(false)) { AllowRepoLess = true };
        h.Server.BlobsSupported = true;
        await h.TickAsync();
        h.RunQueued(j =>
        {
            Attachment(j, "a.txt", "a"u8.ToArray());
            Attachment(j, "b.txt", "b"u8.ToArray());
        });
        h.Server.OnBlobUpload = () => h.Authorities.Set(ChainServer.Url, h.Server.Item.Id, "cancelled", "test");
        await h.TickAsync();
        Assert.Single(h.Server.Blobs);
        Assert.Empty(h.Server.Completions);
        Assert.Single(h.Teams.PendingAttachments(ChainServer.Url, h.Server.Item.Id)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attachment_symlink_and_folder_symlink_are_rejected(bool folderLink)
    {
        using var h = new ChainHarness(Item(false)) { AllowRepoLess = true };
        h.Server.BlobsSupported = true;
        await h.TickAsync();
        h.RunQueued(j =>
        {
            Directory.CreateDirectory(Path.Combine(j.Cwd!, "ticket"));
            if (folderLink) { Directory.CreateSymbolicLink(Path.Combine(j.Cwd!, "ticket", "attachments"), h.Repo); }
            else
            {
                Directory.CreateDirectory(Path.Combine(j.Cwd!, "ticket", "attachments"));
                File.CreateSymbolicLink(Path.Combine(j.Cwd!, "ticket", "attachments", "secret.txt"), Path.Combine(h.Repo, "base.txt"));
            }
        });
        await h.TickAsync();
        Assert.Contains("symlink", Assert.Single(h.Server.Failures));
        Assert.Empty(h.Server.Blobs);
    }

    [Fact]
    public async Task Null_and_omitted_repository_claims_use_scratch_and_complete_without_git_metadata()
    {
        var item = Item(false);
        // Exercise omitted wire property as well as explicit null deserialization.
        Assert.Null(JsonSerializer.Deserialize("{\"repositoryId\":null}", PRFactoryWorkItemJson.Default.PRFactoryWorkItem)!.RepositoryId);
        Assert.Null(JsonSerializer.Deserialize("{}", PRFactoryWorkItemJson.Default.PRFactoryWorkItem)!.RepositoryId);
        item.Type = "Planning";
        using var h = new ChainHarness(item) { AllowRepoLess = true };
        await h.TickAsync();
        var job = Assert.Single(h.RunQueued(j =>
        {
            Assert.StartsWith(h.WorkspaceRoot, j.Cwd!, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(j.Cwd!, ".git")));
            Directory.CreateDirectory(Path.Combine(j.Cwd!, "ticket"));
            File.WriteAllText(Path.Combine(j.Cwd!, "ticket", "plan.md"), "# Scratch plan");
        }));
        var workspace = h.Workspaces.Get($"{ChainServer.Url}|{item.Id:D}")!;
        Assert.Null(workspace.RepositoryId);
        Assert.Null(workspace.InternalBranch);
        Assert.Null(workspace.BaseSha);
        Assert.Equal(job.Cwd, workspace.LeadPath);
        await h.TickAsync();
        Assert.All(h.Server.PollQueries, q => Assert.DoesNotContain("repositoryIds", q));
        Assert.Contains("Scratch plan", Assert.Single(h.Server.Artefacts));
        Assert.DoesNotContain("plan-basis", h.Server.Artefacts[0]);
        var complete = Assert.Single(h.Server.Completions);
        Assert.Equal(JsonValueKind.Null, complete.GetProperty("resultBranch").ValueKind);
        Assert.Equal(JsonValueKind.Null, complete.GetProperty("resultCommitSha").ValueKind);
        Assert.Equal(JsonValueKind.Null, complete.GetProperty("publication").ValueKind);
    }

    [Fact]
    public async Task Repo_less_intake_requires_opt_in()
    {
        using var h = new ChainHarness(Item(false));
        await h.TickAsync();
        Assert.Empty(h.Server.PollQueries);
        Assert.Empty(h.Teams.Pending(ChainServer.Url));
    }
}
