using Fleet.Runtime.Copilot.Sessions;
using Microsoft.Extensions.AI;

namespace Fleet.Runtime.Copilot.Tools;

/// <summary>Supplies the host-implemented (custom) tools for one session.</summary>
public interface ISessionToolProvider
{
    IReadOnlyList<AIFunctionDeclaration> GetTools(FleetSession session, IReadOnlyCollection<string> availableTools);
}
