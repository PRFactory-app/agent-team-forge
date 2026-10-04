using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTeamForge.Host.Features.PRFactory;

// Bitbucket Cloud (REST 2.0). A port of the bitbucket-pr skill's bbpr.py: the stored bitbucket.org git
// credential is a repository/workspace access token, which the REST API only accepts as a Bearer token.
public sealed partial class PRFactoryPullRequests
{
    static readonly Uri BitbucketApi = new("https://api.bitbucket.org/2.0/");

    // BITBUCKET_TOKEN, else the git credential for bitbucket.org. Only the password line is read; nothing is logged.
    async Task<string> BitbucketTokenAsync(string? user, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("BITBUCKET_TOKEN");
        if (string.IsNullOrEmpty(token)) { token = await CredentialFillAsync("bitbucket.org", null, user, ct); }
        if (string.IsNullOrEmpty(token))
        {
            throw new Failure("No credential for bitbucket.org on this machine; store an access token with git or set BITBUCKET_TOKEN.");
        }
        // Never let anything but a plain token become a header value.
        if (!BearerToken().IsMatch(token)) { throw new Failure("The bitbucket.org credential is not a plain token."); }
        return token;
    }

    sealed class Bitbucket(PRFactoryPullRequests owner, string token, string workspace, string repo) : IPullRequestHost
    {
        string Repository => $"repositories/{workspace}/{repo}/";

        public string Name => "Bitbucket";

        public async Task<string> HeadShaAsync(string branch, CancellationToken ct)
        {
            using var document = await SendAsync(HttpMethod.Get, Repository + "refs/branches/" + branch, null, ct);
            return Read(() => document.RootElement.GetProperty("target").GetProperty("hash").GetString() ?? "");
        }

        // Only a same-repository PR for the published branch and base qualifies; forks share branch names.
        public async Task<OpenPullRequest?> FindOpenAsync(PRFactoryPullRequestRequest request, CancellationToken ct)
        {
            var query = $"source.branch.name=\"{request.HeadBranch}\" AND destination.branch.name=\"{request.BaseBranch}\" AND state=\"OPEN\"";
            using var document = await SendAsync(HttpMethod.Get, Repository + "pullrequests?pagelen=50&q=" + Uri.EscapeDataString(query), null, ct);
            return Read(() =>
            {
                var eligible = document.RootElement.GetProperty("values").EnumerateArray()
                    .Where(pr => pr.GetProperty("source").GetProperty("branch").GetProperty("name").GetString() == request.HeadBranch
                        && pr.GetProperty("destination").GetProperty("branch").GetProperty("name").GetString() == request.BaseBranch
                        && string.Equals(pr.GetProperty("source").GetProperty("repository").GetProperty("full_name").GetString(),
                            $"{workspace}/{repo}", StringComparison.OrdinalIgnoreCase))
                    .Select(Pull).OfType<OpenPullRequest>().ToList();
                return eligible.FirstOrDefault(p => SameCommit(p.HeadSha, request.HeadSha)) ?? eligible.FirstOrDefault();
            });
        }

        public async Task<OpenPullRequest?> CreateAsync(PRFactoryPullRequestRequest request, CancellationToken ct)
        {
            using var body = new MemoryStream();
            using (var json = new Utf8JsonWriter(body))
            {
                json.WriteStartObject();
                json.WriteString("title", request.Title);
                json.WriteString("description", request.Body ?? "");
                json.WriteStartObject("source");
                json.WriteStartObject("branch");
                json.WriteString("name", request.HeadBranch);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteStartObject("destination");
                json.WriteStartObject("branch");
                json.WriteString("name", request.BaseBranch);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
            }
            using var document = await SendAsync(HttpMethod.Post, Repository + "pullrequests", body.ToArray(), ct);
            return Read(() => Pull(document.RootElement));
        }

        static OpenPullRequest? Pull(JsonElement pr)
        {
            var url = pr.GetProperty("links").GetProperty("html").GetProperty("href").GetString();
            var hash = pr.GetProperty("source").TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
                ? commit.GetProperty("hash").GetString() ?? "" : "";
            return url is not null && url.StartsWith("https://", StringComparison.Ordinal) ? new(pr.GetProperty("id").GetInt32(), url, hash) : null;
        }

        static T Read<T>(Func<T> read) => ReadJson("Bitbucket", read);

        Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken ct) =>
            owner.SendJsonAsync("Bitbucket", BitbucketApi, path, method, body, token,
                headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", token), ct);
    }

    [GeneratedRegex("^[\\x21-\\x7E]{1,4096}$")] private static partial Regex BearerToken();
}
