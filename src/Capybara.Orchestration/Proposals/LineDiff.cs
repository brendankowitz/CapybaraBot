using System.Text;
using Capybara.Orchestration.Configuration;

namespace Capybara.Orchestration.Proposals;

/// <summary>
/// Minimal unified diff (LCS over lines) for rendering proposals in the approval inbox. Lines split on LF (CRLF is
/// normalized); any other control or invisible character is rendered as a visible <c>&lt;U+XXXX&gt;</c> marker.
/// </summary>
public static class LineDiff
{
    /// <summary>Inputs above this many lines per side are rejected rather than diffed.</summary>
    public const int MaxLines = 4000;

    public static string Unified(string? before, string after, string path, int context = 3)
    {
        ArgumentNullException.ThrowIfNull(after);
        var a = Split(before ?? string.Empty);
        var b = Split(after);
        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            throw new ArgumentException($"Files over {MaxLines} lines cannot be proposed.");
        }

        var ops = Compute(a, b);
        var output = new StringBuilder();
        output.Append("--- a/").Append(path).Append('\n').Append("+++ b/").Append(path).Append('\n');

        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ')
            {
                i++;
                continue;
            }

            var start = Math.Max(0, i - context);
            var end = i;
            var lastChange = i;
            while (end < ops.Count && (ops[end].Kind != ' ' || end - lastChange <= context * 2))
            {
                if (ops[end].Kind != ' ')
                {
                    lastChange = end;
                }

                end++;
            }

            end = Math.Min(ops.Count, lastChange + context + 1);
            var hunk = ops.GetRange(start, end - start);
            var oldStart = hunk[0].OldLine;
            var newStart = hunk[0].NewLine;
            output.Append("@@ -").Append(oldStart + 1).Append(',').Append(hunk.Count(o => o.Kind != '+'))
                .Append(" +").Append(newStart + 1).Append(',').Append(hunk.Count(o => o.Kind != '-')).Append(" @@\n");
            foreach (var op in hunk)
            {
                // Anything a terminal or parser would act on but a reviewer cannot see is shown as <U+XXXX>.
                output.Append(op.Kind).Append(ReviewableText.Escape(op.Text)).Append('\n');
            }

            i = end;
        }

        return output.ToString();
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    private static List<Op> Compute(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var x = a.Length - 1; x >= 0; x--)
        {
            for (var y = b.Length - 1; y >= 0; y--)
            {
                lcs[x, y] = string.Equals(a[x], b[y], StringComparison.Ordinal) ? lcs[x + 1, y + 1] + 1 : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);
            }
        }

        var ops = new List<Op>();
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && string.Equals(a[i], b[j], StringComparison.Ordinal))
            {
                ops.Add(new Op(' ', a[i], i++, j++));
            }
            else if (j < b.Length && (i == a.Length || lcs[i, j + 1] >= lcs[i + 1, j]))
            {
                ops.Add(new Op('+', b[j], i, j++));
            }
            else
            {
                ops.Add(new Op('-', a[i], i++, j));
            }
        }

        return ops;
    }

    private readonly record struct Op(char Kind, string Text, int OldLine, int NewLine);
}
