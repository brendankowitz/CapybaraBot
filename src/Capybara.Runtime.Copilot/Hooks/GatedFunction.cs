using Microsoft.Extensions.AI;

namespace Capybara.Runtime.Copilot.Hooks;

/// <summary>
/// A host-invoked function that refuses to run unless the policy gate allowed a call to it in this session
/// (FR-SAF-01). Broker tools are wrapped in it, so the approval-required gate applies to them even if the runtime
/// were ever to invoke a custom tool without raising <c>OnPreToolUse</c> first (the broker itself does not enforce
/// approval-required tools).
/// </summary>
internal sealed class GatedFunction(AIFunction inner, string sessionId, PermissionAllowances allowances) : DelegatingAIFunction(inner)
{
    public const string NotAuthorized = "denied: not authorized by fleet policy";

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        allowances.TryConsumeInvocation(sessionId, Name)
            ? base.InvokeCoreAsync(arguments, cancellationToken)
            : ValueTask.FromResult<object?>(NotAuthorized);
}
