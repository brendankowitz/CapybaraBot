using System.Buffers;
using System.Globalization;
using System.Text;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Characters a human cannot see in a diff but a parser, model or terminal acts on (FR-SAF-11, FR-SKL-04). Rejected:
/// <list type="bullet">
/// <item>C0/C1 controls (including ESC), lone CR, NEL, line and paragraph separators (YAML line breaks <c>LineDiff</c> would not split on);</item>
/// <item>every format character (zero-width, bidi overrides, BOM, tag characters U+E0000–E007F) and unpaired surrogates;</item>
/// <item>the remaining default-ignorable code points, which render as nothing: variation selectors (U+FE00–FE0D and
/// U+E0100–E01EF always; U+FE0E/FE0F unless directly after a symbol such as an emoji, or in a 0–9/#/* keycap before
/// U+20E3),
/// combining grapheme joiner, Hangul and Mongolian fillers and selectors, U+FFF0–FFF8 and the whole U+E0000–E0FFF block;</item>
/// <item>the braille blank U+2800, the musical blank U+1D159, unassigned code points, and more than <see cref="MaxCombiningMarks"/> combining marks in a row.</item>
/// </list>
/// Tab, LF and CRLF are allowed.
/// </summary>
internal static class ReviewableText
{
    /// <summary>
    /// Longest run of combining marks (Mn/Me) on one base. Real text needs a few; hundreds paint over neighbouring
    /// lines of a rendered diff.
    /// </summary>
    public const int MaxCombiningMarks = 8;

    /// <summary>Describes the first unsafe character, or returns null when <paramref name="text"/> is safe to review.</summary>
    public static string? FindUnsafe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var unit in Scan(text))
        {
            if (!unit.Allowed)
            {
                return unit.Value < 0 ? $"unpaired surrogate at offset {unit.Index}" : $"{Describe(unit.Value)} at offset {unit.Index}";
            }
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
        var builder = new StringBuilder(line.Length + 16);
        foreach (var unit in Scan(line))
        {
            // In a single line even CRLF and LF are out of place, so every control except tab is escaped.
            var visible = unit.Allowed && (unit.Value == '\t' || !(unit.Value < 0x20 || unit.Value is 0x7F));
            if (visible)
            {
                builder.Append(line, unit.Index, unit.Length);
            }
            else
            {
                builder.Append(unit.Value < 0 ? Describe(line[unit.Index]) : Describe(unit.Value));
            }
        }

        return builder.ToString();
    }

    /// <summary>One code point (or unpaired surrogate, <c>Value</c> = -1) and whether it may appear in reviewed text.</summary>
    private readonly record struct Unit(int Index, int Length, int Value, bool Allowed);

    private static IEnumerable<Unit> Scan(string text)
    {
        var index = 0;
        Rune? previous = null;
        var marks = 0;
        while (index < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
            {
                yield return new Unit(index, 1, -1, false);
                previous = null;
                marks = 0;
                index++;
                continue;
            }

            marks = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark ? marks + 1 : 0;
            yield return new Unit(index, consumed, rune.Value, IsAllowed(rune, previous, marks, text, index + consumed));
            previous = rune;
            index += consumed;
        }
    }

    /// <summary>
    /// Whether <paramref name="rune"/> may appear here. <paramref name="marks"/> is the length of the run of combining
    /// marks it ends (0 when it is not a mark); <paramref name="next"/> is the offset of the following code unit in
    /// <paramref name="text"/>, for CRLF and keycap look-ahead; <paramref name="previous"/> is the preceding code point.
    /// </summary>
    private static bool IsAllowed(Rune rune, Rune? previous, int marks, string text, int next)
    {
        var value = rune.Value;
        switch (value)
        {
            case '\t' or '\n':
                return true;
            case '\r':
                return next < text.Length && text[next] == '\n';
            case >= 0xFE00 and <= 0xFE0D:
                // Only the emoji (FE0F) and text (FE0E) presentation selectors have a visible effect; the rest can
                // carry a hidden payload after any letter.
                return false;
            case 0xFE0E or 0xFE0F:
                return previous is { } baseRune && (Rune.GetUnicodeCategory(baseRune) == UnicodeCategory.OtherSymbol
                    || (baseRune.Value is (>= '0' and <= '9') or '#' or '*' && next < text.Length && text[next] == '\u20E3'));
        }

        if (marks > MaxCombiningMarks)
        {
            return false;
        }

        return !IsInvisible(value) && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned);
    }

    /// <summary>Default-ignorable (DerivedCoreProperties) code points that are not format characters, plus blanks that render as nothing (braille U+2800, musical U+1D159).</summary>
    private static bool IsInvisible(int value) => value is 0x034F
        or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F) or 0x2065 or 0x2800 or 0x3164 or 0xFFA0
        or (>= 0xFFF0 and <= 0xFFF8) or 0x1D159 or (>= 0xE0000 and <= 0xE0FFF);

    private static string Describe(int value) => string.Create(CultureInfo.InvariantCulture, $"<U+{value:X4}>");
}