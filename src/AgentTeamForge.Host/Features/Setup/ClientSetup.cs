using AgentTeamForge.DAL.Files;
using System.Text.Json.Nodes;
using System.Text.Json;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Reconciles only AgentTeamForge's entries in installed agent clients.</summary>
internal static class ClientSetup
{
    const string Name = "agentteamforge";
    const string Adapter = "npm:pi-mcp-adapter";

    internal static string StableBinary(string executable, string home)
    {
        var binary = Path.GetFullPath(executable);
        var stable = Path.Combine(home, ".local", "bin", "atf");
        var current = Path.Combine(home, ".local", "share", "agentteamforge", "current");
        try
        {
            var link = new FileInfo(stable).LinkTarget;
            var release = new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName;
            if (link is not null && release is not null
                && Path.GetFullPath(Path.Combine(Path.GetDirectoryName(stable)!, link)) == Path.Combine(current, "atf")
                && (binary == stable || binary == Path.Combine(release, "atf")))
            {
                return stable;
            }
        }
        catch (IOException) { }
        return binary;
    }

    internal static bool Reconcile(string binary, string stateDir, string home, string claudeSettings,
        string? extensionOverride, Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> run, bool apply)
    {
        var healthy = true;
        foreach (var client in new[] { "claude", "codex" })
        {
            if (run(client, ["--version"]).ExitCode == 127)
            {
                Console.Out.WriteLine($"{client}: not installed (optional)");
                continue;
            }

            var (exitCode, output) = run(client, ["mcp", "get", Name]);
            var desiredArgs = $"mcp --state-dir {stateDir}";
            var found = exitCode == 0;
            var matches = found && HasLine(output, "Command:", binary)
                && HasLine(output, "Args:", desiredArgs)
                && (client != "claude" || output.Contains("Scope: User config", StringComparison.Ordinal))
                && (client != "codex" || HasLine(output, "enabled:", "true"));
            if (matches)
            {
                Console.Out.WriteLine($"{client}: MCP registration current");
                continue;
            }

            if (!apply)
            {
                Console.Out.WriteLine($"{client}: MCP registration missing or stale");
                healthy = false;
                continue;
            }

            // Claude refuses to overwrite a named entry. Remove only its user-scoped entry.
            if (client == "claude" && found && output.Contains("Scope: User config", StringComparison.Ordinal)
                && run(client, ["mcp", "remove", Name, "--scope", "user"]).ExitCode != 0)
            {
                Console.Error.WriteLine("error: claude MCP removal failed");
                return false;
            }
            var args = client == "claude"
                ? (IReadOnlyList<string>)["mcp", "add", "--scope", "user", Name, "--", binary, "mcp", "--state-dir", stateDir]
                : ["mcp", "add", Name, "--", binary, "mcp", "--state-dir", stateDir];
            if (run(client, args).ExitCode != 0)
            {
                Console.Error.WriteLine($"error: {client} MCP registration failed");
                return false;
            }
            Console.Out.WriteLine($"{client}: MCP registration updated");
        }

        if (!OperatingSystem.IsWindows() && run("claude", ["--version"]).ExitCode != 127)
        {
            try
            {
                if (!ClaudeInboundCurrent(claudeSettings))
                {
                    if (apply)
                    {
                        SetupCommand.EnableClaudeInbound(claudeSettings);
                        Console.Out.WriteLine("claude: crossSessionInbound enabled");
                    }
                    else
                    {
                        Console.Out.WriteLine("claude: crossSessionInbound missing");
                        healthy = false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"error: Claude settings invalid ({ex.GetType().Name})");
                return false;
            }
        }

        if (run("pi", ["--version"]).ExitCode != 127)
        {
            try
            {
                healthy &= ReconcilePi(binary, stateDir, home, extensionOverride, run, apply);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"error: Pi setup failed ({ex.GetType().Name})");
                return false;
            }
        }
        else
        {
            Console.Out.WriteLine("pi: not installed (optional)");
        }
        return healthy;
    }

    internal static bool Teardown(string binary, string stateDir, string home,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> run)
    {
        foreach (var client in new[] { "claude", "codex" })
        {
            var (code, output) = run(client, ["mcp", "get", Name]);
            if (code != 0 || !HasLine(output, "Command:", binary)
                || !HasLine(output, "Args:", $"mcp --state-dir {stateDir}")
                || client == "claude" && !output.Contains("Scope: User config", StringComparison.Ordinal))
            {
                continue;
            }
            var args = client == "claude"
                ? (IReadOnlyList<string>)["mcp", "remove", Name, "--scope", "user"]
                : ["mcp", "remove", Name];
            if (run(client, args).ExitCode != 0)
            {
                Console.Error.WriteLine($"error: {client} MCP removal failed");
                return false;
            }
            Console.Out.WriteLine($"{client}: removed ATF MCP registration");
        }

        try
        {
            var directory = Path.Combine(home, ".pi", "agent");
            var settingsPath = Path.Combine(directory, "settings.json");
            var mcpPath = Path.Combine(directory, "mcp.json");
            var statePath = Path.Combine(directory, "agentteamforge.json");
            var extension = Path.Combine(home, ".local", "share", "agentteamforge", "current", "extensions", "pi-wake");
            var settings = ReadObject(settingsPath);
            if (settings["packages"] is JsonArray packages)
            {
                var owned = packages.Where(node => PackageSource(node) is string source && IsLocalPackageSource(source)
                    && Path.GetFullPath(source, directory).Equals(extension, StringComparison.Ordinal)).ToArray();
                foreach (var entry in owned)
                {
                    packages.Remove(entry);
                }
                if (owned.Length > 0)
                {
                    WriteObject(settingsPath, settings);
                    Console.Out.WriteLine("pi: removed ATF wake extension");
                }
            }
            var mcp = ReadObject(mcpPath);
            if (mcp["mcpServers"] is JsonObject servers
                && JsonNode.DeepEquals(servers[Name], new JsonObject
                {
                    ["command"] = binary,
                    ["args"] = new JsonArray("mcp", "--state-dir", stateDir),
                }))
            {
                servers.Remove(Name);
                WriteObject(mcpPath, mcp);
                Console.Out.WriteLine("pi: removed ATF MCP registration");
            }
            var state = ReadObject(statePath);
            if (state["stateDir"]?.GetValueKind() == JsonValueKind.String
                && state["stateDir"]!.GetValue<string>() == stateDir)
            {
                state.Remove("stateDir");
                WriteObject(statePath, state);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"error: Pi teardown failed ({ex.GetType().Name})");
            return false;
        }
    }

    static bool ReconcilePi(string binary, string stateDir, string home, string? extensionOverride,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> run, bool apply)
    {
        var directory = Path.Combine(home, ".pi", "agent");
        var settingsPath = Path.Combine(directory, "settings.json");
        var mcpPath = Path.Combine(directory, "mcp.json");
        var statePath = Path.Combine(directory, "agentteamforge.json");
        var extension = extensionOverride ?? Path.Combine(home, ".local", "share", "agentteamforge", "current", "extensions", "pi-wake");
        if (extensionOverride is null && !File.Exists(Path.Combine(extension, "package.json")))
        {
            extension = Path.Combine(Path.GetDirectoryName(binary)!, "extensions", "pi-wake");
        }
        extension = Path.GetFullPath(extension);

        var settings = ReadObject(settingsPath);
        var packages = settings["packages"] as JsonArray;
        var sources = packages?.Select(PackageSource).OfType<string>().ToList() ?? [];
        var adapterCurrent = sources.Contains(Adapter);
        var extensionCurrent = sources.Any(source => IsLocalPackageSource(source)
            && Path.GetFullPath(source, directory).Equals(extension,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var mcp = ReadObject(mcpPath);
        var expected = new JsonObject
        {
            ["command"] = binary,
            ["args"] = new JsonArray("mcp", "--state-dir", stateDir),
        };
        var mcpCurrent = JsonNode.DeepEquals((mcp["mcpServers"] as JsonObject)?[Name], expected);
        var state = ReadObject(statePath);
        var stateCurrent = state["stateDir"]?.GetValueKind() == JsonValueKind.String
            && state["stateDir"]!.GetValue<string>() == stateDir;
        var present = adapterCurrent && extensionCurrent && mcpCurrent && stateCurrent;
        if (present)
        {
            Console.Out.WriteLine("pi: adapter, MCP and wake extension current");
            return true;
        }
        if (!apply)
        {
            var missing = new List<string>();
            if (!adapterCurrent)
            {
                missing.Add("MCP adapter");
            }
            if (!extensionCurrent)
            {
                missing.Add("wake extension");
            }
            if (!mcpCurrent)
            {
                missing.Add("MCP registration");
            }
            if (!stateCurrent)
            {
                missing.Add("state directory setting");
            }
            Console.Out.WriteLine($"pi: {string.Join(", ", missing)} missing or stale");
            return false;
        }
        if (!File.Exists(Path.Combine(extension, "package.json")))
        {
            Console.Error.WriteLine($"error: bundled Pi wake extension missing at {extension}");
            return false;
        }
        if (!adapterCurrent && run("pi", ["install", Adapter]).ExitCode != 0)
        {
            Console.Error.WriteLine("error: Pi MCP adapter install failed");
            return false;
        }
        if (!extensionCurrent && run("pi", ["install", extension]).ExitCode != 0)
        {
            Console.Error.WriteLine("error: Pi wake extension install failed");
            return false;
        }
        var servers = mcp["mcpServers"] as JsonObject ?? [];
        servers[Name] = expected;
        mcp["mcpServers"] = servers;
        if (!mcpCurrent)
        {
            WriteObject(mcpPath, mcp);
        }
        if (!stateCurrent)
        {
            state["stateDir"] = stateDir;
            WriteObject(statePath, state);
        }
        Console.Out.WriteLine("pi: adapter, MCP and wake extension updated");
        return true;
    }

    // Pi package entries are either a source string or an object with a "source" string.
    static string? PackageSource(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var source) => source,
        JsonObject entry when entry["source"] is JsonValue value && value.TryGetValue<string>(out var source) => source,
        _ => null,
    };

    internal static bool IsLocalPackageSource(string source) => !source.Contains(':', StringComparison.Ordinal)
        || source.Length >= 3 && char.IsAsciiLetter(source[0]) && source[1] == ':' && source[2] is '\\' or '/';

    static bool HasLine(string output, string label, string value) => output.Split('\n').Any(line =>
        line.Trim().StartsWith(label + " ", StringComparison.OrdinalIgnoreCase)
        && line.Trim()[(label.Length + 1)..].Equals(value, StringComparison.Ordinal));

    static bool ClaudeInboundCurrent(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        using var document = JsonDocument.Parse(File.ReadAllBytes(path),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("crossSessionInbound", out var inbound)
            && inbound.ValueKind == JsonValueKind.String && inbound.GetString() == "accept";
    }

    static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new JsonException($"Expected object in {path}");
    }

    static void WriteObject(string path, JsonObject value)
    {
        StateDirectory.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write)))
            {
                using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
                value.WriteTo(writer);
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
