namespace AgentTeamForge.Host.Hosting;

internal static class CommandLine
{
    // Options null = internal command (spawned by ATF with fixed args): help only, no validation.
    // A "+" prefix marks a presence-only flag: a bare word after it is a stray argument, not its value.
    static readonly (string Command, string Usage, string[]? Options)[] Commands =
    [
        ("setup", "setup [--mode headless|herdr|terminal|wt] [--web-port PORT] [--idle-close-minutes MINUTES|off] [--max-retained-sessions COUNT] [--autostart[=off]] [--state-dir DIR] [--check|--apply] [--force]",
            ["mode", "web-port", "idle-close-minutes", "max-retained-sessions", "autostart", "state-dir", "+check", "+apply", "+force"]),
        ("doctor", "doctor [--state-dir DIR]", ["state-dir"]),
        ("start", "start [--state-dir DIR]", ["state-dir"]),
        ("stop", "stop [--state-dir DIR]", ["state-dir"]),
        ("web", "web [--open] [--rotate-token] [--state-dir DIR]", ["+open", "+rotate-token", "state-dir", "port"]),
        ("uninstall", "uninstall [--purge] [--force] [--state-dir DIR]", ["+purge", "+force", "state-dir", "+teardown-only"]),
        ("prfactory connect", "prfactory connect --url HTTPS_URL [--repo ID=DIR]... [--external ID:MEMBER]... [--token-scope tenant-wide|repository] [--repo-less true|false] [--state-dir DIR] (token on stdin)",
            ["url", "repo", "external", "token", "token-scope", "repo-less", "state-dir"]),
        ("prfactory disconnect", "prfactory disconnect [--state-dir DIR]", ["state-dir"]),
        ("prfactory status", "prfactory status [--state-dir DIR]", ["state-dir"]),
        ("worktrees prune", "worktrees prune [--job ID] [--dry-run] [--force (needs --job)] [--state-dir DIR]", ["job", "+dry-run", "+force", "state-dir"]),
        ("prune", "prune [--older-than 30d] [--dry-run] [--state-dir DIR]", ["older-than", "+dry-run", "state-dir"]),
        ("init", "init --state-dir DIR [--test-profile] [--queue-limit N] [--max-runtime-seconds N] [--backends LIST]", null),
        ("daemon", "daemon --state-dir DIR", null),
        ("mcp", "mcp --state-dir DIR [--managed-context FILE]", null),
        ("client", "client ACTION [JOB] --state-dir DIR [options]", null),
        ("fake-backend", "fake-backend", null),
    ];

    public static string Usage => "usage: atf --version | "
        + string.Join(" | ", Commands.Where(c => c.Options is not null).Select(c => c.Usage))
        + " | <init|daemon|mcp|client|fake-backend> --state-dir DIR [options]";

    // Returns null to dispatch normally, otherwise the exit code.
    public static int? Check(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args[0] is "--help" or "-h" or "help")
        {
            stdout.WriteLine(Usage);
            return 0;
        }

        var entry = args.Length > 1 ? Commands.FirstOrDefault(c => c.Command == args[0] + " " + args[1]) : default;
        if (entry.Command is null)
        {
            entry = Commands.FirstOrDefault(c => c.Command == args[0]);
        }

        if (entry.Command is null)
        {
            var group = Commands.Where(c => c.Command.StartsWith(args[0] + " ", StringComparison.Ordinal)).ToArray();
            if (group.Length > 0 && args.Any(IsHelp))
            {
                foreach (var c in group)
                {
                    stdout.WriteLine("usage: atf " + c.Usage);
                }

                return 0;
            }

            return null;
        }

        var rest = args.Skip(entry.Command.Count(ch => ch == ' ') + 1).ToArray();
        if (rest.Any(IsHelp))
        {
            stdout.WriteLine("usage: atf " + entry.Usage);
            return 0;
        }

        if (entry.Options is null)
        {
            return null;
        }

        for (var i = 0; i < rest.Length; i++)
        {
            var token = rest[i];
            string? error = null;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var name = token[2..];
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    name = name[..equals];
                }

                var isFlag = entry.Options.Contains("+" + name);
                if (!isFlag && !entry.Options.Contains(name))
                {
                    error = $"unknown option --{name}";
                }
                else if (isFlag && equals >= 0)
                {
                    error = $"option --{name} takes no value ({token})";
                }
                else if (equals < 0 && !isFlag && i + 1 < rest.Length && !rest[i + 1].StartsWith('-'))
                {
                    i++;
                }
            }
            else
            {
                error = token.StartsWith('-') ? $"unknown option {token}" : $"unexpected argument '{token}'";
            }

            if (error is not null)
            {
                stderr.WriteLine($"error: {error} for atf {entry.Command}");
                stderr.WriteLine("usage: atf " + entry.Usage);
                return 64;
            }
        }

        return null;
    }

    static bool IsHelp(string a) => a is "--help" or "-h";
}
