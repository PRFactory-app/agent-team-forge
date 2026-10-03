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

    /// <summary>Exit code a command runner returns when the tool did not finish in time (never a real exit code).</summary>
    internal const int TimedOut = -1;

    internal static string NoResponse(string tool, TimeSpan timeout) =>
        $"{tool} did not respond within {timeout.TotalSeconds:0} s; the {tool} on PATH may be a looping wrapper";

    /// <summary>
    /// The installer's stable link, as written from HOME, when <paramref name="executable"/> is the
    /// release it currently points at. The OS may report the executable fully link-resolved
    /// (macOS: /private/tmp/... for HOME=/tmp/...), so the comparison is on canonical paths.
    /// </summary>
    internal static string StableBinary(string executable, string home)
    {
        var binary = Path.GetFullPath(executable);
        var stable = Path.Combine(home, ".local", "bin", "atf");
        var root = Path.Combine(home, ".local", "share", "agentteamforge");
        var current = Path.Combine(root, "current");
        try
        {
            if (new FileInfo(stable).LinkTarget is { } link && new DirectoryInfo(current).LinkTarget is not null
                && Path.GetFullPath(link, Path.GetDirectoryName(stable)!) is var target
                && Path.GetFileName(target) == "atf" && Path.GetFileName(Path.GetDirectoryName(target)) == "current"
                && CanonicalPath.Same(Path.GetDirectoryName(Path.GetDirectoryName(target))!, root)
                && CanonicalPath.Same(binary, Path.Combine(current, "atf")))
            {
                return stable;
            }
        }
        catch (IOException) { }
        return binary;
    }

    // A stale bridge from an older release must not start an old daemon over current state.
    internal static string CurrentRelease(string executable, string home)
    {
        var root = Path.Combine(home, ".local", "share", "agentteamforge");
        var binary = Path.GetFullPath(executable);
        if (!CanonicalPath.Within(binary, Path.Combine(root, "releases")))
        {
            return executable;
        }
        try
        {
            var current = Path.Combine(root, "current");
            var release = new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName ?? current;
            var target = Path.Combine(release, "atf");
            return File.Exists(target) ? target : executable;
        }
        catch (IOException) { return executable; }
    }

    internal static bool Reconcile(string binary, string stateDir, string home, string claudeSettings,
        string? extensionOverride, Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> run, bool apply,
        bool registrationFailureNonFatal = false)
    {
        var healthy = true;
        var hardFailure = false;
        var installed = 0;
        var versions = new Dictionary<string, (int ExitCode, string Output)>(StringComparer.Ordinal);
        // Each client is probed once; one that does not answer is reported and skipped, never waited on again.
        (int ExitCode, string Output) Version(string tool)
        {
            if (!versions.TryGetValue(tool, out var result))
            {
                versions[tool] = result = run(tool, ["--version"]);
                if (result.ExitCode == TimedOut)
                {
                    Console.Error.WriteLine($"{tool}: failed ({result.Output})");
                    healthy = false;
                }
            }
            return result;
        }
        Console.Out.WriteLine($"MCP binary: {binary}");
        Console.Out.WriteLine($"State directory: {stateDir}");
        foreach (var client in new[] { "claude", "codex" })
        {
            var probe = Version(client).ExitCode;
            if (probe == 127)
            {
                Console.Out.WriteLine($"{client}: skipped (not installed)");
                continue;
            }
            installed++;
            if (probe == TimedOut)
            {
                continue;
            }

            var (exitCode, output) = run(client, ["mcp", "get", Name]);
            var desiredArgs = StateArgs + stateDir;
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

            var args = client == "claude"
                ? (IReadOnlyList<string>)["mcp", "add", "--scope", "user", Name, "--", binary, "mcp", "--state-dir", stateDir]
                : ["mcp", "add", Name, "--", binary, "mcp", "--state-dir", stateDir];
            var removeArgs = (IReadOnlyList<string>)["mcp", "remove", Name, "--scope", "user"];
            var removeFirst = false;
            // Claude refuses to overwrite a named entry. Remove only its user-scoped entry.
            if (client == "claude" && found && output.Contains("Scope: User config", StringComparison.Ordinal))
            {
                removeFirst = true;
                var (removalExit, removalOutput) = run(client, removeArgs);
                if (removalExit != 0)
                {
                    Console.Error.WriteLine($"claude: failed (MCP removal: {BoundedError(removalOutput)})");
                    Console.Error.WriteLine(RegisterLater(client, removeArgs, args));
                    healthy = false;
                    continue;
                }
            }
            var (registrationExit, registrationOutput) = run(client, args);
            if (registrationExit != 0)
            {
                Console.Error.WriteLine($"{client}: failed (MCP registration: {BoundedError(registrationOutput)})");
                Console.Error.WriteLine(RegisterLater(client, removeFirst ? removeArgs : null, args));
                healthy = false;
                continue;
            }
            Console.Out.WriteLine($"{client}: installed (MCP registration updated)");
        }

        if (Version("claude").ExitCode is not (127 or TimedOut))
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
                        hardFailure = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"claude: failed (settings: {BoundedError(ex.Message)})");
                healthy = false;
                hardFailure = true;
            }
        }

        var piProbe = Version("pi").ExitCode;
        if (piProbe == TimedOut)
        {
            installed++;
        }
        else if (piProbe != 127)
        {
            installed++;
            try
            {
                if (!ReconcilePi(binary, stateDir, home, extensionOverride, run, apply))
                {
                    healthy = false;
                    hardFailure = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"pi: failed (setup: {BoundedError(ex.Message)})");
                healthy = false;
                hardFailure = true;
            }
        }
        else if (piProbe == 127)
        {
            Console.Out.WriteLine("pi: skipped (not installed)");
        }
        // Cursor and Droid are managed headless agents. Their MCP commands edit
        // user/project configuration, so setup only detects them here.
        foreach (var (client, binaryName) in new[] { ("cursor", "cursor-agent"), ("droid", "droid") })
        {
            var (code, version) = Version(binaryName);
            if (code == TimedOut)
            {
                continue;
            }
            Console.Out.WriteLine(code == 127 ? $"{client}: skipped (not installed)"
                : code == 0 ? $"{client}: installed ({BoundedError(version)}; headless-only)"
                : $"{client}: found but version check failed ({BoundedError(version)})");
        }
        if (installed == 0)
        {
            Console.Out.WriteLine($"{(apply ? "Setup succeeded, but no" : "No")} agent clients were found. Install and log in to Claude, Codex, or Pi, then rerun atf setup.");
        }
        if (!healthy && !apply)
        {
            // Check mode changed nothing, so there is nothing written and nothing to reload.
            Console.Error.WriteLine("warning: some client registrations are missing or stale (see above); run atf setup to repair them.");
        }
        else if (!healthy)
        {
            Console.Error.WriteLine("warning: some client registrations failed (see above); launch config is written. Fix the cause, then rerun atf setup or run the commands above; atf setup --check verifies.");
            Console.Out.WriteLine("Reload clients whose MCP registration succeeded.");
        }
        else if (installed > 0 && apply)
        {
            Console.Out.WriteLine("Reload installed clients to use AgentTeamForge.");
        }
        return registrationFailureNonFatal ? !hardFailure : healthy;
    }

    static string RegisterLater(string client, IReadOnlyList<string>? removeArgs, IReadOnlyList<string> addArgs)
    {
        static string Line(string client, IReadOnlyList<string> args) =>
            client + " " + string.Join(' ', args.Select(ShellQuote));
        return "  register later: " + (removeArgs is null ? "" : Line(client, removeArgs) + "; ") + Line(client, addArgs);
    }

    static string ShellQuote(string value) =>
        OperatingSystem.IsWindows() ? AgentTeamForge.Business.Features.Agents.Terminals.PowerShellText.Quote(value)
        : value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "_-./:=@%+".Contains(c)) ? value
        : "'" + value.Replace("'", "'\\''") + "'";

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
            // Ownership compares canonical paths: an older setup may have registered the same
            // binary or state through a differently spelled (link-resolved) path.
            if (code != 0 || !LineValues(output, "Command:").Any(command => SamePath(command, binary))
                || !LineValues(output, "Args:").Any(args => args.StartsWith(StateArgs, StringComparison.Ordinal)
                    && SamePath(args[StateArgs.Length..], stateDir))
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
                    && CanonicalPath.Same(Path.GetFullPath(source, directory), extension)).ToArray();
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
                && servers[Name] is JsonObject { Count: 2 } registration
                && registration["command"]?.GetValueKind() == JsonValueKind.String && SamePath(registration["command"]!.GetValue<string>(), binary)
                && registration["args"] is JsonArray { Count: 3 } args
                && args.All(arg => arg?.GetValueKind() == JsonValueKind.String)
                && args[0]!.GetValue<string>() == "mcp" && args[1]!.GetValue<string>() == "--state-dir"
                && SamePath(args[2]!.GetValue<string>(), stateDir))
            {
                servers.Remove(Name);
                WriteObject(mcpPath, mcp);
                Console.Out.WriteLine("pi: removed ATF MCP registration");
            }
            var state = ReadObject(statePath);
            if (state["stateDir"]?.GetValueKind() == JsonValueKind.String
                && SamePath(state["stateDir"]!.GetValue<string>(), stateDir))
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

    const string StateArgs = "mcp --state-dir ";

    static bool HasLine(string output, string label, string value) =>
        LineValues(output, label).Any(found => found.Equals(value, StringComparison.Ordinal));

    static IEnumerable<string> LineValues(string output, string label) => output.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase))
        .Select(line => line[(label.Length + 1)..]);

    /// <summary>Only absolute paths are compared; a relative one in a client config is not ours.</summary>
    static bool SamePath(string registered, string expected) =>
        Path.IsPathFullyQualified(registered) && CanonicalPath.Same(registered, expected);

    static bool ClaudeInboundCurrent(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        using var document = ReadClaudeSettings(path);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("crossSessionInbound", out var inbound)
            && inbound.ValueKind == JsonValueKind.String && inbound.GetString() == "accept";
    }

    internal static JsonDocument ReadClaudeSettings(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return JsonDocument.Parse(bytes.AsMemory(offset),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
