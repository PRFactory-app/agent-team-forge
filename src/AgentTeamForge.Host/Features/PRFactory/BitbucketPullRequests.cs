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
        if (string.IsNullOrEmpty(token))
        {
            var input = "protocol=https\nhost=bitbucket.org\n" + (user is null ? "" : $"username={user}\n") + "\n";
            // Unattended on every platform: no terminal prompt, and no Git Credential Manager sign-in window
            // (Windows/macOS default helper). Output may end lines with CRLF there.
            var env = new Dictionary<string, string?>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
                ["GIT_ASKPASS"] = null,
                ["SSH_ASKPASS"] = null
            };
            ProcessResult result;
            try { result = await runner(new ProcessSpec("git", ["credential", "fill"], env, input, TokenTimeout), ct); }
            catch (TimeoutException) { throw new Failure($"git credential fill did not finish within {TokenTimeout.TotalSeconds:0} s."); }
            catch (System.ComponentModel.Win32Exception) { throw new Failure("git is not installed or not on PATH on this machine."); }
            token = result.ExitCode != 0 ? null : result.Stdout.Split('\n')
                .FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..].TrimEnd('\r');
        }
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

        static T Read<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
            {
                throw new Failure("Bitbucket returned an unreadable response.");
            }
        }

        async Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken ct)
        {
            using var http = new HttpClient(owner.httpHandler ?? new HttpClientHandler(), disposeHandler: owner.httpHandler is null)
            {
                BaseAddress = BitbucketApi,
                Timeout = CallTimeout
            };
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            try
            {
                using var response = await http.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    var details = Scrub(text, token);
                    throw new Failure($"Bitbucket refused: HTTP {(int)response.StatusCode} {method} {path.Split('?')[0]}",
                        details[..Math.Min(details.Length, 2000)]);
                }
                try { return JsonDocument.Parse(text); }
                catch (JsonException) { throw new Failure("Bitbucket returned an unreadable response."); }
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Failure($"Bitbucket did not answer within {CallTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                throw new Failure("Could not reach Bitbucket: " + (ex.HttpRequestError == HttpRequestError.Unknown ? ex.GetType().Name : ex.HttpRequestError));
            }
        }
    }

    [GeneratedRegex("^[\\x21-\\x7E]{1,4096}$")] private static partial Regex BearerToken();
}
