using Capybara.Memory.Abstractions;
using Capybara.Memory.Access;
using Capybara.Policy.Provenance;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// The one place that decides which memory a session touches (FR-MEM-05, FR-MEM-12). An incognito session gets
/// <see cref="NullMemoryStore"/> <em>and</em> an incognito access context, so even a store that ignored the first
/// would refuse through the second.
/// </summary>
public static class SessionMemory
{
    public static IMemoryStore StoreFor(CapybaraSession session, IMemoryStore memory)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.IsIncognito ? NullMemoryStore.Instance : memory;
    }

    public static MemoryAccessContext ContextFor(CapybaraSession session, TaintTracker taint)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(taint);
        var context = new MemoryAccessContext
        {
            Agent = session.Agent.Id,
            SessionId = session.SessionId,
            ReadOnly = session.IsReadOnly,
            CanReadUser = session.Agent.ReadsUserMemory,
            SessionTaint = taint.Of(session.SessionId),
        };
        return session.IsIncognito ? context.AsIncognito() : context;
    }
}
