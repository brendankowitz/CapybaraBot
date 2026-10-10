using System.Buffers;
using System.Globalization;
using System.Text;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Characters a human cannot see in a diff but a parser, model or terminal acts on (FR-SAF-11, FR-SKL-04). Rejected:
/// <list type="bullet">
/// <item>C0/C1 controls (including ESC), lone CR, NEL, line and paragraph separators (YAML line breaks <c>LineDiff</c> would not split on);</item>
/// <item>every format character (zero-width, bidi overrides, BOM, tag characters U+E0000–E007F) and unpaired surrogates;</item>
/// <item>the remaining default-ignorable code points, which render as nothing: variation selectors (U+FE00–FE0F are
/// allowed only singly, directly after a visible base character, as in emoji presentation; U+E0100–E01EF never),
/// combining grapheme joiner, Hangul and Mongolian fillers and selectors, U+FFF0–FFF8 and the whole U+E0000–E0FFF block;</item>
/// <item>the braille blank U+2800, and unassigned code points.</item>
/// </list>
/// Tab, LF and CRLF are allowed.
/// </summary>
internal static class ReviewableText
{
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
        while (index < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
            {
                yield return new Unit(index, 1, -1, false);
                previous = null;
                index++;
                continue;
            }

            yield return new Unit(index, consumed, rune.Value, IsAllowed(rune, previous, text, index));
            previous = rune;
            index += consumed;
        }
    }

    private static bool IsAllowed(Rune rune, Rune? previous, string text, int index)
    {
        var value = rune.Value;
        switch (value)
        {
            case '\t' or '\n':
                return true;
            case '\r':
                return index + 1 < text.Length && text[index + 1] == '\n';
            case >= 0xFE00 and <= 0xFE0F:
                // Emoji/text presentation selector: one, directly after a visible base character.
                return previous is { } baseRune && IsVisibleBase(baseRune);
        }

        return !IsInvisible(value) && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned);
    }

    private static bool IsVisibleBase(Rune rune) =>
        !IsInvisible(rune.Value) && rune.Value is not (>= 0xFE00 and <= 0xFE0F) && !Rune.IsWhiteSpace(rune)
        && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or UnicodeCategory.NonSpacingMark);

    /// <summary>Default-ignorable (DerivedCoreProperties) code points that are not format characters, plus the braille blank.</summary>
    private static bool IsInvisible(int value) => value is 0x034F
        or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F) or 0x2065 or 0x2800 or 0x3164 or 0xFFA0
        or (>= 0xFFF0 and <= 0xFFF8) or (>= 0xE0000 and <= 0xE0FFF);

    private static string Describe(int value) => string.Create(CultureInfo.InvariantCulture, $"<U+{value:X4}>");
}