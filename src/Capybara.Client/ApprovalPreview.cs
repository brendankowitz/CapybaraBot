using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capybara.Client;

/// <summary>An email an agent wants to send, reduced to inert text. No markup survives, so nothing can load or link (FR-SAF-07).</summary>
// RemoteImagesBlocked: Number of <c>&lt;img&gt;</c> tags removed from an HTML body.
// LinksStripped: Number of <c>&lt;a&gt;</c> tags removed; their text stays, their targets never render.
public sealed record EmailPreview(string To, string? Cc, string Subject, string BodyText, int RemoteImagesBlocked, int LinksStripped);

/// <summary>
/// Structured view of an approval card, derived from the server's text preview <c>"{agent} → {tool} {args json}"</c>.
/// Everything here is untrusted agent output: pages must bind it as text nodes only.
/// </summary>
// Kind: <c>email</c>, <c>diff</c> or <c>args</c>.
// Truncated: The server cut the preview at 2000 chars, so the arguments could not be fully parsed.
public sealed partial record ApprovalPreview(string Kind, EmailPreview? Email, string? Diff, IReadOnlyList<KeyValuePair<string, string>> Args, string Raw, bool Truncated)
{
    private static readonly string[] EmailKeys = ["to", "subject", "body"];

    public static ApprovalPreview Parse(ApprovalDto approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var raw = approval.Preview;
        var jsonStart = raw.IndexOf('{', StringComparison.Ordinal);
        if (jsonStart < 0)
        {
            return new ApprovalPreview("args", null, null, [], raw, Truncated: false);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(raw[jsonStart..]);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ApprovalPreview("args", null, null, [], raw, Truncated: true);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ApprovalPreview("args", null, null, [], raw, Truncated: false);
        }

        var args = root.EnumerateObject().Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText())).ToList();
        var byName = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);

        var looksLikeEmail = approval.Tool.Contains("mail", StringComparison.OrdinalIgnoreCase) && EmailKeys.Any(byName.ContainsKey);
        if (looksLikeEmail)
        {
            var rest = args.Where(a => !EmailKeys.Contains(a.Key, StringComparer.OrdinalIgnoreCase) && !a.Key.Equals("cc", StringComparison.OrdinalIgnoreCase)).ToList();
            return new ApprovalPreview("email", ToEmail(byName), null, rest, raw, Truncated: false);
        }

        var diff = args.FirstOrDefault(a => a.Key.Equals("diff", StringComparison.OrdinalIgnoreCase) || a.Key.Equals("patch", StringComparison.OrdinalIgnoreCase) || LooksLikeUnifiedDiff(a.Value));
        if (diff.Key is not null)
        {
            return new ApprovalPreview("diff", null, diff.Value, args.Where(a => a.Key != diff.Key).ToList(), raw, Truncated: false);
        }

        return new ApprovalPreview("args", null, null, args, raw, Truncated: false);
    }

    private static bool LooksLikeUnifiedDiff(string value) =>
        value.StartsWith("diff --git", StringComparison.Ordinal) || value.StartsWith("--- ", StringComparison.Ordinal) || value.Contains("\n@@ ", StringComparison.Ordinal);

    private static EmailPreview ToEmail(Dictionary<string, string> args)
    {
        var body = args.GetValueOrDefault("body") ?? string.Empty;
        var images = ImageTag().Count(body);
        var links = AnchorTag().Count(body);
        return new EmailPreview(
            args.GetValueOrDefault("to") ?? string.Empty,
            args.GetValueOrDefault("cc"),
            args.GetValueOrDefault("subject") ?? string.Empty,
            HtmlToText(body),
            images,
            links);
    }

    /// <summary>Strips every tag and decodes entities. Output is only ever bound as text, never as markup.</summary>
    internal static string HtmlToText(string body)
    {
        if (!body.Contains('<', StringComparison.Ordinal))
        {
            return body;
        }

        var text = ScriptOrStyle().Replace(body, string.Empty);
        text = LineBreak().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

    [GeneratedRegex(@"<img\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"<a\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTag();

    [GeneratedRegex(@"<(script|style)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<br\s*/?>|</p\s*>|</div\s*>|</li\s*>|</h[1-6]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</?[a-zA-Z!][^>]*>?")]
    private static partial Regex AnyTag();
}
