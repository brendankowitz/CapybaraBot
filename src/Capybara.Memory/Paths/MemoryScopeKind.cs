namespace Capybara.Memory.Paths;

/// <summary>The three wiki scopes (FR-MEM-01).</summary>
public enum MemoryScopeKind
{
    /// <summary><c>/user/</c>: profile, people, preferences (PARA folders).</summary>
    User,

    /// <summary><c>/agents/{id}/</c>: learned procedures and domain notes.</summary>
    Agent,

    /// <summary><c>/projects/{id}/</c>: decisions and conventions shared by participating agents.</summary>
    Project,
}
