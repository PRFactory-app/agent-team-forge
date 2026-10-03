using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentTeamForge.Host.Features.PRFactory;

// Azure DevOps Services (REST api-version 7.1) with a personal access token as Basic ":PAT".
public sealed partial class PRFactoryPullRequests
{
    // AZURE_DEVOPS_PAT, else the git credential for the repository URL (Git Credential Manager keys dev.azure.com
    // credentials by path), else the host-wide one a plain store helper keeps.
    async Task<string> AzureDevOpsTokenAsync(RemoteRepository remote, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT");
        if (string.IsNullOrEmpty(token))
        {
            var path = $"{remote.Owner}/{Uri.EscapeDataString(remote.Project!)}/_git/{Uri.EscapeDataString(remote.Repo)}";
            token = await CredentialFillAsync("dev.azure.com", path, remote.User, ct);
            if (string.IsNullOrEmpty(token)) { token = await CredentialFillAsync("dev.azure.com", null, remote.User, ct); }
        }
        if (string.IsNullOrEmpty(token))
        {
            throw new Failure("No credential for dev.azure.com on this machine; store a personal access token with git or set AZURE_DEVOPS_PAT.");
        }
        // Never let anything but a plain token become a header value.
        if (!BearerToken().IsMatch(token)) { throw new Failure("The dev.azure.com credential is not a plain token."); }
        return token;
    }

    sealed class AzureDevOps(PRFactoryPullRequests owner, string token, string organization, string project, string repo) : IPullRequestHost
    {
        const string Version = "api-version=7.1";
        // Azure DevOps limits a pull request description to 4000 characters.
        const int MaxDescription = 4000;

        Uri Api => new($"https://dev.azure.com/{organization}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/");

        public string Name => "Azure DevOps";

        // The filter is a prefix match, so only the exact ref counts; a missing branch reads as moved.
        public async Task<string> HeadShaAsync(string branch, CancellationToken ct)
        {
            using var document = await SendAsync(HttpMethod.Get, $"refs?filter={Uri.EscapeDataString("heads/" + branch)}&{Version}", null, ct);
            return Read(() => document.RootElement.GetProperty("value").EnumerateArray()
                .Where(r => r.GetProperty("name").GetString() == "refs/heads/" + branch)
                .Select(r => r.GetProperty("objectId").GetString() ?? "").FirstOrDefault() ?? "");
        }

        // Only a same-repository PR for the published branch and base qualifies; a fork PR carries forkSource.
        public async Task<OpenPullRequest?> FindOpenAsync(PRFactoryPullRequestRequest request, CancellationToken ct)
        {
            var query = $"pullrequests?searchCriteria.sourceRefName={Uri.EscapeDataString("refs/heads/" + request.HeadBranch)}"
                + $"&searchCriteria.targetRefName={Uri.EscapeDataString("refs/heads/" + request.BaseBranch)}&searchCriteria.status=active&{Version}";
            using var document = await SendAsync(HttpMethod.Get, query, null, ct);
            return Read(() =>
            {
                var eligible = document.RootElement.GetProperty("value").EnumerateArray()
                    .Where(pr => pr.GetProperty("sourceRefName").GetString() == "refs/heads/" + request.HeadBranch
                        && pr.GetProperty("targetRefName").GetString() == "refs/heads/" + request.BaseBranch
                        && !pr.TryGetProperty("forkSource", out _))
                    .Select(Pull).ToList();
                return eligible.FirstOrDefault(p => SameCommit(p.HeadSha, request.HeadSha)) ?? eligible.FirstOrDefault();
            });
        }

        public async Task<OpenPullRequest?> CreateAsync(PRFactoryPullRequestRequest request, CancellationToken ct)
        {
            var description = request.Body ?? "";
            using var body = new MemoryStream();
            using (var json = new Utf8JsonWriter(body))
            {
                json.WriteStartObject();
                json.WriteString("sourceRefName", "refs/heads/" + request.HeadBranch);
                json.WriteString("targetRefName", "refs/heads/" + request.BaseBranch);
                json.WriteString("title", request.Title);
                json.WriteString("description", description.Length > MaxDescription ? description[..MaxDescription] : description);
                json.WriteEndObject();
            }
            using var document = await SendAsync(HttpMethod.Post, "pullrequests?" + Version, body.ToArray(), ct);
            // Without the source commit yet, list again rather than report an unknown head.
            return Read(() => Pull(document.RootElement) is { HeadSha.Length: > 0 } created ? created : null);
        }

        // The API url is a REST resource; people need the web page.
        OpenPullRequest Pull(JsonElement pr)
        {
            var id = pr.GetProperty("pullRequestId").GetInt32();
            var hash = pr.TryGetProperty("lastMergeSourceCommit", out var commit) && commit.ValueKind == JsonValueKind.Object
                ? commit.GetProperty("commitId").GetString() ?? "" : "";
            return new(id, $"https://dev.azure.com/{organization}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repo)}/pullrequest/{id}", hash);
        }

        static T Read<T>(Func<T> read) => ReadJson("Azure DevOps", read);

        // X-TFS-FedAuthRedirect: Suppress turns a rejected token into 401 instead of a 203 sign-in page.
        Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken ct) =>
            owner.SendJsonAsync("Azure DevOps", Api, path, method, body, token, headers =>
            {
                headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
                headers.Add("X-TFS-FedAuthRedirect", "Suppress");
            }, ct);
    }
}
