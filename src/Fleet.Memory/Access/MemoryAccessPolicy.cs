using Fleet.Memory.Paths;

namespace Fleet.Memory.Access;

/// <summary>Scope access rules enforced in code (FR-MEM-05).</summary>
public static class MemoryAccessPolicy
{
    public static bool CanRead(MemoryAccessContext context, MemoryPath path)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);
        return !context.Incognito && path.Scope switch
        {
            MemoryScopeKind.User => context.CanReadUser,
            MemoryScopeKind.Agent => path.Owner == context.Agent.Value,
            MemoryScopeKind.Project => path.Owner == context.ProjectId,
            _ => false,
        };
    }

    /// <summary>
    /// Direct writes: only <c>/agents/{self}/</c> and the active project. <c>/user/</c> never accepts a direct write;
    /// it goes through the steward path as a reviewable diff.
    /// </summary>
    public static bool CanWriteDirectly(MemoryAccessContext context, MemoryPath path)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);
        return !context.Incognito && !context.ReadOnly && path.Scope switch
        {
            MemoryScopeKind.Agent => path.Owner == context.Agent.Value,
            MemoryScopeKind.Project => context.ProjectId is not null && path.Owner == context.ProjectId,
            _ => false,
        };
    }

    public static bool CanProposeUserChange(MemoryAccessContext context, MemoryPath path) =>
        !context.Incognito && !context.ReadOnly && path.Scope == MemoryScopeKind.User;
}
