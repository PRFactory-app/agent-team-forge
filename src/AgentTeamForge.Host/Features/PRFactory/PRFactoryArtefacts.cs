using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

internal static class PRFactoryArtefacts
{
    // Below PRFactory's default request body limit; the whole request is frozen in SQLite.
    internal const long MaxUploadBytes = 20 * 1024 * 1024;

    public static async Task<List<PRFactoryArtefactFile>> CollectAsync(PRFactoryWorkItem item, string cwd, string? resultText, CancellationToken ct)
    {
        var files = new List<PRFactoryArtefactFile>();
        long total = 0;
        if (item.Type == "CustomStep" && string.IsNullOrWhiteSpace(item.ExpectedOutput))
        { throw new InvalidDataException("CustomStep is missing ExpectedOutput in the claim"); }
        if (!string.IsNullOrWhiteSpace(item.TicketArtefactFolder))
        {
            var folder = SafePath(cwd, item.TicketArtefactFolder);
            if (item.ExpectedOutput is { Length: > 0 } output)
            {
                if (Path.GetFileName(output) != output) { throw new InvalidDataException("ExpectedOutput must be a filename"); }
                SafePath(cwd, Path.Combine(item.TicketArtefactFolder, output));
            }
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
                {
                    if (item.Type == "CustomStep" && !string.Equals(Path.GetFileName(file), item.ExpectedOutput, StringComparison.OrdinalIgnoreCase)) { continue; }
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension is not (".md" or ".html") && !(item.Type == "Decomposition" && extension == ".json")
                        && Path.GetFileName(file) != item.ExpectedOutput) { continue; }
                    SafePath(cwd, Path.GetRelativePath(cwd, file));
                    if ((total += new FileInfo(file).Length) > MaxUploadBytes)
                    { throw new InvalidDataException($"artefacts exceed {MaxUploadBytes / (1024 * 1024)} MB at {Path.GetFileName(file)}"); }
                    files.Add(new(Path.GetFileName(file), await File.ReadAllTextAsync(file, ct), Kind(Path.GetFileName(file), item.Type)));
                }
            }
        }
        // PRFactory's built-in Phase 0 draft prompt asks for JSON on stdout rather than files.
        // Materialize that contract into the same artefacts as its local worker so the normal
        // upload and checkpoint validation still apply.
        if (item.Type == "TicketRefinement" && IsPrdDraft(item) && !files.Any(f => f.Kind == "prd")
            && TryPrdResult(resultText, out var prd, out var questions))
        {
            files.Add(new("prd.md", prd, "prd"));
            if (questions.Count > 0)
            {
                var qa = "# Product-owner questions\n\n" + string.Join("\n\n", questions.Select((q, i) =>
                    $"## Q{i + 1} [{q.Category}]\n\n{q.Text}\n\n**Answer:**"));
                files.Add(new("qa.md", qa + "\n", "qa-po"));
            }
        }
        var required = RequiredKinds(item);
        if (required.Length > 0 && !files.Any(f => required.Contains(f.Kind, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"{item.Type} requires {string.Join(" or ", required)} output in {item.TicketArtefactFolder}; found: {string.Join(", ", files.Select(f => f.FileName))}");
        }
        if (item.ExpectedOutput is { Length: > 0 } expected && !files.Any(f => string.Equals(f.FileName, expected, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Missing required output {item.TicketArtefactFolder}/{expected}");
        }
        if (item.RepositoryId is { } repositoryId && item.Type == "Planning" && JobWorktree.Head(cwd) is { } head)
        {
            var paths = await JobWorktree.TrackedPathsAsync(cwd, ct);
            if (paths is not null)
            {
                var manifest = new PRFactoryPlanBasis([new(repositoryId, RepositoryName(item, cwd), JobWorktree.Branch(cwd), head, paths)]);
                files.Add(new("plan-basis.json", JsonSerializer.Serialize(manifest, PRFactoryWorkItemJson.Default.PRFactoryPlanBasis), "plan-basis"));
            }
        }
        return files;
    }

    static bool IsPrdDraft(PRFactoryWorkItem item)
    {
        try
        {
            using var context = JsonDocument.Parse(item.ContextJson ?? "{}");
            return context.RootElement.TryGetProperty("phase", out var phase)
                && phase.GetString() == "prd";
        }
        catch (JsonException) { return false; }
    }

    static bool TryPrdResult(string? resultText, out string prd, out List<(string Text, string Category)> questions)
    {
        prd = string.Empty;
        questions = [];
        if (string.IsNullOrWhiteSpace(resultText)) { return false; }
        var json = resultText.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = json.IndexOf('\n');
            var closing = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine < 0 || closing <= firstLine) { return false; }
            json = json[(firstLine + 1)..closing].Trim();
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("prd_markdown", out var value) || value.ValueKind != JsonValueKind.String)
            { return false; }
            prd = value.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prd)) { return false; }
            if (root.TryGetProperty("questions", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var question in array.EnumerateArray())
                {
                    var text = question.GetProperty("text").GetString();
                    var category = question.GetProperty("category").GetString();
                    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(category)) { return false; }
                    questions.Add((text, category));
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    static string RepositoryName(PRFactoryWorkItem item, string cwd)
    {
        try
        {
            using var context = JsonDocument.Parse(item.ContextJson ?? "{}");
            return context.RootElement.GetProperty("repositories").GetProperty("primary").GetProperty("name").GetString() ?? Path.GetFileName(cwd);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return Path.GetFileName(cwd); }
    }

    internal static string SafePath(string cwd, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains(".."))
        { throw new InvalidDataException("artefact path is outside the mapped repository"); }
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new InvalidDataException("artefact path is outside the mapped repository"); }
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is not null)
            { throw new InvalidDataException($"artefact symlink is forbidden: {relative}"); }
        }
        return path;
    }

    static string Kind(string file, string? type)
    {
        if (type == "CustomStep") { return "custom-step"; }
        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (name is "qa" or "questions") { return type == "TicketRefinement" ? "qa-po" : "qa-dev"; }
        if (name.StartsWith("plan-review", StringComparison.Ordinal)) { return "plan-review"; }
        if (name.StartsWith("plan", StringComparison.Ordinal)) { return "plan"; }
        if (name.StartsWith("code-review", StringComparison.Ordinal)) { return "code-review"; }
        return name;
    }

    static string[] RequiredKinds(PRFactoryWorkItem item)
    {
        string? step = null, phase = null;
        try
        {
            using var context = JsonDocument.Parse(item.ContextJson ?? "{}");
            if (context.RootElement.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.String) { step = s.GetString(); }
            if (context.RootElement.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String) { phase = p.GetString(); }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return item.Type switch
        {
            "TicketRefinement" when step == "classify" => ["classification"],
            "TicketRefinement" when phase == "prd" => ["prd", "qa-po"],
            "TicketRefinement" when phase == "refinement" => ["ticket"],
            "TicketRefinement" => ["ticket", "prd", "classification", "qa-po"],
            "ClarifyingQuestions" => ["qa-dev", "qa-po"],
            "Planning" => ["plan"],
            "PlanReview" => ["plan-review"],
            "TestPlan" => ["testplan"],
            "CodeReview" => ["code-review"],
            "VisualQa" => ["visual-qa"],
            "Decomposition" => ["decomposition-proposal"],
            "CustomStep" => ["custom-step"],
            "Discovery" => step switch
            {
                "Intake" => ["idea"],
                "Research" => ["research"],
                "BusinessCase" => ["business-case"],
                "RequirementDefinition" => ["ticket"],
                "TechnicalRefinement" => ["tech-refinement"],
                _ => ["idea", "research", "business-case", "ticket", "tech-refinement", "compliance-review"]
            },
            _ => []
        };
    }
}

internal sealed record PRFactoryPlanBasis(List<PRFactoryPlanRepository> Repositories);
internal sealed record PRFactoryPlanRepository(Guid RepositoryId, string Name, string? Branch, string HeadSha, string[] Paths);
