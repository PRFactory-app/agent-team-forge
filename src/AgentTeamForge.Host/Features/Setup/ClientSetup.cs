using AgentTeamForge.DAL.Files;
using System.Text.Json.Nodes;
using System.Text.Json;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Reconciles only AgentTeamForge's entries in installed agent clients.</summary>
internal static class ClientSetup
{
    const string Name = "agentteamforge";
    const string Adapter = PiMcpAdapter.Package;

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
        var installed = 0;
        Console.Out.WriteLine($"MCP binary: {binary}");
        Console.Out.WriteLine($"State directory: {stateDir}");
        foreach (var client in new[] { "claude", "codex" })
        {
            if (run(client, ["--version"]).ExitCode == 127)
            {
                Console.Out.WriteLine($"{client}: skipped (not installed)");
                continue;
            }
            installed++;

            var (exitCode, output) = run(client, ["mcp", "get", Name]);
            var desiredArgs = $"mcp --state-dir {stateDir}";
            var found = exitCode == 0;
            var matches = found && HasLine(output, "Command:", binary)
                && HasLine(output, "Args:", desiredArgs)
                && (client != "claude" || output.Contains("Scope: User config", StringComparison.Ordinal))
                && (client != "codex" || HasLine(output, "enabled:", "true"));
            if (matches)
            {
                Console.Out.WriteLine($"{client}: installed (MCP registration current)");
                continue;
            }

            if (!apply)
            {
                Console.Out.WriteLine($"{client}: failed (MCP registration missing or stale)");
                healthy = false;
                continue;
            }

            // Claude refuses to overwrite a named entry. Remove only its user-scoped entry.
            if (client == "claude" && found && output.Contains("Scope: User config", StringComparison.Ordinal))
            {
                var (removalExit, removalOutput) = run(client, ["mcp", "remove", Name, "--scope", "user"]);
                if (removalExit != 0)
                {
                    Console.Error.WriteLine($"claude: failed (MCP removal: {BoundedError(removalOutput)})");
                    healthy = false;
                    continue;
                }
            }
            var args = client == "claude"
                ? (IReadOnlyList<string>)["mcp", "add", "--scope", "user", Name, "--", binary, "mcp", "--state-dir", stateDir]
                : ["mcp", "add", Name, "--", binary, "mcp", "--state-dir", stateDir];
            var (registrationExit, registrationOutput) = run(client, args);
            if (registrationExit != 0)
            {
                Console.Error.WriteLine($"{client}: failed (MCP registration: {BoundedError(registrationOutput)})");
                healthy = false;
                continue;
            }
            Console.Out.WriteLine($"{client}: installed (MCP registration updated)");
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
                Console.Error.WriteLine($"claude: failed (settings: {BoundedError(ex.Message)})");
                healthy = false;
            }
        }

        if (run("pi", ["--version"]).ExitCode != 127)
        {
            installed++;
            try
            {
                healthy &= ReconcilePi(binary, stateDir, home, extensionOverride, run, apply);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"pi: failed (setup: {BoundedError(ex.Message)})");
                healthy = false;
            }
        }
        else
        {
            Console.Out.WriteLine("pi: skipped (not installed)");
        }
        if (installed == 0)
        {
            Console.Out.WriteLine($"{(apply ? "Setup succeeded, but no" : "No")} agent clients were found. Install and log in to Claude, Codex, or Pi, then rerun atf setup.");
        }
        if (!healthy)
        {
            Console.Error.WriteLine("Rerun atf setup after fixing the failed client; successful registrations are kept.");
            Console.Out.WriteLine("Reload clients whose MCP registration succeeded.");
        }
        else if (installed > 0)
        {
            Console.Out.WriteLine("Reload installed clients to use AgentTeamForge.");
        }
        return healthy;
    }

    internal static string BoundedError(string output)
    {
        var useful = string.Join(' ', output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return useful.Length == 0 ? "command exited without details" : useful.Length <= 400 ? useful : useful[..400] + "…";
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
                var owned = packages.Where(node => PiMcpAdapter.PackageSource(node) is string source && IsLocalPackageSource(source)
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
        var sources = packages?.Select(PiMcpAdapter.PackageSource).OfType<string>().ToList() ?? [];
        var adapterCurrent = PiMcpAdapter.IsInstalled(home);
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
            Console.Out.WriteLine("pi: installed (adapter, MCP and wake extension current)");
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
            Console.Out.WriteLine($"pi: failed ({string.Join(", ", missing)} missing or stale)");
            return false;
        }
        if (!File.Exists(Path.Combine(extension, "package.json")))
        {
            Console.Error.WriteLine($"pi: failed (bundled wake extension missing at {extension})");
            return false;
        }
        if (!adapterCurrent && run("pi", ["install", Adapter]) is var adapterInstall && adapterInstall.ExitCode != 0)
        {
            Console.Error.WriteLine($"pi: failed (MCP adapter installation requires network: {BoundedError(adapterInstall.Output)})");
            return false;
        }
        if (!extensionCurrent && run("pi", ["install", extension]) is var extensionInstall && extensionInstall.ExitCode != 0)
        {
            Console.Error.WriteLine($"pi: failed (wake extension installation: {BoundedError(extensionInstall.Output)})");
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
        Console.Out.WriteLine("pi: installed (adapter, MCP and wake extension updated)");
        return true;
    }

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
