using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Host.Features.PRFactory;

internal static class PRFactoryAttachments
{
    internal const int MaxFileBytes = 10 * 1024 * 1024;
    internal const int MaxAttemptBytes = 50 * 1024 * 1024;

    public static async Task<List<PRFactoryAttachment>> CollectAsync(PRFactoryTeamRecord team, PRFactoryWorkItem item,
        string cwd, PublicationReceipt? publication, CancellationToken ct)
    {
        var uploads = new List<PRFactoryAttachment>();
        long total = 0;
        async Task Add(string kind, string fileName, string mediaType, byte[] bytes,
            string? baseSha = null, string? headSha = null, string? summary = null, bool truncated = false)
        {
            if (bytes.Length > MaxFileBytes || (total += bytes.Length) > MaxAttemptBytes)
            { throw new InvalidDataException($"Attachment limit exceeded at {fileName} (10 MiB/file, 50 MiB/attempt)"); }
            if (team.MachineId is not { } machine || team.AtfJobId is not { Length: > 0 } job
                || item.LeaseToken is not { } lease)
            { throw new InvalidDataException("Attachments require durable machine/job/lease identity"); }
            if (fileName.Length > 255 || fileName.Contains("..", StringComparison.Ordinal)
                || fileName.IndexOfAny(['/', '\\', ':']) >= 0 || fileName.Any(char.IsControl))
            { throw new InvalidDataException("Attachment filename must be a safe basename"); }
            var key = Guid.NewGuid().ToString("N");
            using var form = new MultipartFormDataContent("atf-" + key);
            void Field(string name, string value) => form.Add(new StringContent(value, Encoding.UTF8), name);
            Field("MachineId", machine.ToString("D"));
            Field("JobId", job);
            Field("LeaseToken", lease.ToString("D"));
            Field("Attempt", item.AttemptCount.ToString(CultureInfo.InvariantCulture));
            Field("ClientKey", key);
            Field("RepositoryId", (item.RepositoryId ?? Guid.Empty).ToString("D"));
            Field("Kind", kind);
            Field("FileName", fileName);
            Field("MediaType", mediaType);
            Field("ByteCount", bytes.Length.ToString(CultureInfo.InvariantCulture));
            Field("Sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Field("IsTruncated", truncated ? "true" : "false");
            if (baseSha is not null) { Field("BaseCommitSha", baseSha); }
            if (headSha is not null) { Field("HeadCommitSha", headSha); }
            if (summary is not null) { Field("BinaryChangeSummary", summary); }
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            form.Add(file, "File", fileName);
            uploads.Add(new(key, form.Headers.ContentType!.ToString(), await form.ReadAsByteArrayAsync(ct)));
        }

        if (publication is not null)
        {
            var intent = publication.Intent;
            var range = intent.BaseSha + "..." + intent.HeadSha;
            var (patch, truncated) = await GitOutput(cwd, MaxFileBytes, ct, "diff", "--no-color", "--no-ext-diff", "--no-textconv", range, "--");
            // Keep the truncated patch valid UTF-8 even when the limit bisects a code point.
            var utf8 = new UTF8Encoding(false, true);
            if (truncated)
            {
                var length = patch.Length;
                while (length > patch.Length - 4)
                {
                    try { _ = utf8.GetCharCount(patch, 0, length); break; }
                    catch (DecoderFallbackException) { length--; }
                }
                if (length != patch.Length) { patch = patch[..length]; }
            }
            // The server only accepts NUL-free UTF-8 text; legacy-encoded sources are replaced lossily.
            var lossy = false;
            try { _ = utf8.GetCharCount(patch); lossy = Array.IndexOf(patch, (byte)0) >= 0; }
            catch (DecoderFallbackException) { lossy = true; }
            if (lossy)
            {
                patch = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(patch).Replace('\0', '\uFFFD'));
                if (patch.Length > MaxFileBytes)
                {
                    var length = MaxFileBytes;
                    while ((patch[length] & 0xC0) == 0x80) { length--; } // Cut before a continuation byte.
                    (patch, truncated) = (patch[..length], true);
                }
            }
            var (stats, statsTruncated) = await GitOutput(cwd, 64 * 1024, ct, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--numstat", range, "--");
            var binary = string.Join('\n', Encoding.UTF8.GetString(stats).Split('\n').Where(l => l.StartsWith("-\t-\t", StringComparison.Ordinal)));
            var summary = (truncated ? "Patch truncated at 10 MiB. " : "") + (lossy ? "Non-UTF-8 bytes replaced. " : "")
                + (binary.Length == 0 ? "No binary changes in scanned diff statistics." : binary)
                + (statsTruncated ? "\nStatistics truncated." : "");
            if (summary.Length > 2000) { summary = summary[..1980] + "\nSummary truncated."; }
            await Add("diff", "changes.patch", "text/x-diff", patch, intent.BaseSha, intent.HeadSha, summary, truncated);
        }
        if (!string.IsNullOrWhiteSpace(item.TicketArtefactFolder))
        {
            var relative = Path.Combine(item.TicketArtefactFolder, "attachments");
            var folder = PRFactoryArtefacts.SafePath(cwd, relative);
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
                {
                    var media = AttachmentFiles.MediaType(path);
                    if (media is null) { continue; }
                    PRFactoryArtefacts.SafePath(cwd, Path.GetRelativePath(cwd, path));
                    await using var stream = File.OpenRead(path);
                    if (stream.Length > MaxFileBytes || total + stream.Length > MaxAttemptBytes)
                    { throw new InvalidDataException($"Attachment limit exceeded at {Path.GetFileName(path)} (10 MiB/file, 50 MiB/attempt)"); }
                    var (bytes, oversized) = await ReadCapped(stream, MaxFileBytes, ct);
                    if (oversized) { throw new InvalidDataException($"Attachment exceeds 10 MiB: {Path.GetFileName(path)}"); }
                    await Add("attachment", Path.GetFileName(path), media, bytes);
                }
            }
        }
        return uploads;
    }

    static async Task<(byte[] Bytes, bool Truncated)> GitOutput(string cwd, int cap, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = NonInteractiveProcess.Start(start) ?? throw new InvalidDataException("Cannot generate attachment diff");
        try
        {
            var error = ReadCapped(process.StandardError.BaseStream, 2000, timeout.Token);
            var output = await ReadCapped(process.StandardOutput.BaseStream, cap, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var (diagnostic, _) = await error;
            if (process.ExitCode != 0) { throw new InvalidDataException("Cannot generate attachment diff: " + Encoding.UTF8.GetString(diagnostic)); }
            return output;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    static async Task<(byte[] Bytes, bool Truncated)> ReadCapped(Stream stream, int cap, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var keep = Math.Min(read, cap - (int)bytes.Length);
            bytes.Write(buffer, 0, keep);
            truncated |= keep != read;
        }
        return (bytes.ToArray(), truncated);
    }
}
