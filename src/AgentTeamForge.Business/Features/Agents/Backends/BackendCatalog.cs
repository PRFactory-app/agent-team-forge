namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>The one place a job's backend name is mapped to an <see cref="IJobBackend"/>.</summary>
public sealed class BackendCatalog
{
    public const string Fake = "fake";
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Pi = "pi";
    public const string Cursor = "cursor";
    public const string Droid = "droid";

    readonly Dictionary<string, Func<IJobBackend>> _factories = [];

    public IReadOnlyCollection<string> Names => _factories.Keys;

    public BackendCatalog Register(string name, Func<IJobBackend> factory)
    {
        _factories[name] = factory;
        return this;
    }

    public bool Contains(string name) => _factories.ContainsKey(name);

    public IJobBackend? Resolve(string name) => _factories.TryGetValue(name, out var factory) ? factory() : null;

    /// <summary>
    /// Daemon catalog: the fake backend always; real agent CLIs unless this is
    /// a test profile (tests must never launch a real model).
    /// </summary>
    public static BackendCatalog Create(IJobBackend fake, bool includeRealAgents)
    {
        var catalog = new BackendCatalog().Register(Fake, () => fake);
        if (includeRealAgents)
        {
            catalog.Register(Claude, () => new ClaudeCodeBackend());
            catalog.Register(Codex, () => new CodexExecBackend());
            catalog.Register(Pi, () => new PiBackend());
            catalog.Register(Cursor, () => new CursorCliBackend());
            catalog.Register(Droid, () => new DroidBackend());
        }

        return catalog;
    }
}
