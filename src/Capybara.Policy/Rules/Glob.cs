namespace Capybara.Policy.Rules;

/// <summary>Minimal ordinal glob matcher supporting <c>*</c> and <c>?</c>.</summary>
public static class Glob
{
    public static bool IsMatch(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v]))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
