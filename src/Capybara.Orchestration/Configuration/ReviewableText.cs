using System.Buffers;
using System.Globalization;
using System.Text;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Characters a human cannot see in a diff but a parser or terminal acts on (FR-SAF-11, FR-SKL-04): C0/C1 controls
/// (including ESC), lone CR, NEL, line/paragraph separators (YAML line breaks that <c>LineDiff</c> would not split
/// on), every format character (zero-width, bidi overrides, BOM, tag characters U+E0000–E007F) and unpaired
/// surrogates. Tab, LF and CRLF are allowed.
/// </summary>
internal static class ReviewableText
{
    /// <summary>Describes the first unsafe character, or returns null when <paramref name="text"/> is safe to review.</summary>
    public static string? FindUnsafe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            if (Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed) != OperationStatus.Done)
            {
                return $"unpaired surrogate at offset {index}";
            }

            if (!IsAllowed(rune, span, index))
            {
                return $"{Describe(rune)} at offset {index}";
            }

            index += consumed;
        }

        return null;
    }

    /// <summary>
    /// Renders a single line for display: unsafe characters become visible <c>&lt;U+XXXX&gt;</c> markers, so whatever
    /// reaches a diff or a log cannot hide content or drive a terminal.
    /// </summary>
    public static string Escape(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (FindUnsafe(line) is null)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + 16);
        var span = line.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            if (Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed) != OperationStatus.Done)
            {
                builder.Append(CultureInfo.InvariantCulture, $"<U+{(int)span[index]:X4}>");
                index++;
                continue;
            }

            // In a single line even CRLF and LF are out of place, so escape every control except tab.
            if (rune.Value == '\t' || (IsAllowed(rune, span, index) && !Rune.IsControl(rune)))
            {
                builder.Append(span.Slice(index, consumed));
            }
            else
            {
                builder.Append(Describe(rune));
            }

            index += consumed;
        }

        return builder.ToString();
    }

    private static bool IsAllowed(Rune rune, ReadOnlySpan<char> text, int index) => rune.Value switch
    {
        '\t' or '\n' => true,
        '\r' => index + 1 < text.Length && text[index + 1] == '\n',
        _ => Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate),
    };

    private static string Describe(Rune rune) => string.Create(CultureInfo.InvariantCulture, $"<U+{rune.Value:X4}>");
}
