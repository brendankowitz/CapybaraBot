namespace Fleet.Policy.Rules;

/// <summary>Deterministic rule outcomes. Precedence: Deny &gt; Ask &gt; AllowUnlessTainted &gt; Allow.</summary>
public enum RuleEffect
{
    Allow = 0,

    /// <summary>Allow in a clean session; require approval in a tainted one unless the destination is allowlisted (FR-SAF-03).</summary>
    AllowUnlessTainted = 1,

    Ask = 2,

    Deny = 3,
}
