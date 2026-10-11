namespace Capybara.Policy.Review;

public enum ReviewVerdict
{
    Allow,
    Escalate,
    Block,
}

public sealed record ReviewResult(ReviewVerdict Verdict, string Rationale);
