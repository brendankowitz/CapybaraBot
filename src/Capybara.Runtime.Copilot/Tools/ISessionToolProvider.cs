using Capybara.Runtime.Copilot.Sessions;
using Microsoft.Extensions.AI;

namespace Capybara.Runtime.Copilot.Tools;

/// <summary>Supplies the host-implemented (custom) tools for one session.</summary>
public interface ISessionToolProvider
{
    IReadOnlyList<AIFunctionDeclaration> GetTools(CapybaraSession session, IReadOnlyCollection<string> availableTools);
}
