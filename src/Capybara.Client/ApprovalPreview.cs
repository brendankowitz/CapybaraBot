using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capybara.Client;

/// <summary>
/// An email an agent wants to send, reduced to inert text. No markup survives, so nothing can load or link (FR-SAF-07),
/// but nothing is hidden from the approver either: every link/image target and every piece of content an email client
/// would not show (script, style, comments) is listed as plain text.
/// </summary>
/// <param name="To">Recipient as supplied.</param>
/// <param name="Cc">Cc as supplied, if any.</param>
/// <param name="Subject">Subject as supplied.</param>
/// <param name="BodyText">The body with tags removed and entities decoded.</param>
/// <param name="RemoteImagesBlocked">Number of image tags removed.</param>
/// <param name="LinksStripped">Number of anchor tags removed.</param>
/// <param name="References">Every URL-bearing attribute of the stripped tags, as text, e.g. <c>img src: http://x/p.gif</c>.</param>
/// <param name="HiddenContent">Content a mail client would not display (script/style bodies and comments), verbatim.</param>
public sealed record EmailPreview(
    string To,
    string? Cc,
    string Subject,
    string BodyText,
    int RemoteImagesBlocked,
    int LinksStripped,
    IReadOnlyList<string> References,
    IReadOnlyList<string> HiddenContent);

/// <summary>
/// Structured view of an approval card, derived from the full args JSON when the server supplies it
/// (<see cref="ApprovalDto.ArgsJson"/>), else from the server's text preview <c>"{agent} → {tool} {args json}"</c>.
/// Everything here is untrusted agent output: pages must bind it as text nodes only.
/// </summary>
/// <remarks>
/// Parsing never throws: any failure degrades to kind <c>args</c> with only <see cref="Exact"/> shown.
/// A card whose arguments could not be seen in full or unambiguously cannot be approved (<see cref="CanApprove"/>).
/// </remarks>
/// <param name="Kind"><c>email</c>, <c>diff</c> or <c>args</c>.</param>
/// <param name="Email">Set when <paramref name="Kind"/> is <c>email</c>.</param>
/// <param name="Diff">Set when <paramref name="Kind"/> is <c>diff</c>.</param>
/// <param name="Args">Every argument not shown by the email or diff view, in server order.</param>
/// <param name="Exact">The exact text the arguments were parsed from, shown verbatim to the approver.</param>
/// <param name="Truncated">The server cut the preview mid-JSON and no full args were supplied.</param>
/// <param name="ParseFailed">The arguments were not valid JSON or not an object.</param>
/// <param name="ConflictingKeys">Argument names that collide case-insensitively (for example <c>to</c> and <c>To</c>).</param>
public sealed partial record ApprovalPreview(
    string Kind,
    EmailPreview? Email,
    string? Diff,
    IReadOnlyList<KeyValuePair<string, string>> Args,
    string Exact,
    bool Truncated,
    bool ParseFailed,
    IReadOnlyCollection<string> ConflictingKeys)
{
    private static readonly string[] EmailKeys = ["to", "subject", "body"];

    /// <summary>Why the card can only be denied, or <see langword="null"/> when the arguments were seen in full.</summary>
    public string? BlockReason =>
        Truncated ? "The preview is cut off, so some arguments (such as the recipient) are not shown."
        : ParseFailed ? "The arguments could not be parsed."
        : ConflictingKeys.Count > 0 ? $"Argument names are ambiguous: {string.Join(", ", ConflictingKeys)}."
        : null;

    public bool CanApprove => BlockReason is null;

    public static ApprovalPreview Parse(ApprovalDto approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var hasFull = !string.IsNullOrWhiteSpace(approval.ArgsJson);
        var exact = hasFull ? approval.ArgsJson! : approval.Preview;
        try
        {
            return ParseCore(approval.Tool, exact, hasFull);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or RegexMatchTimeoutException)
        {
            return new ApprovalPreview("args", null, null, [], exact, Truncated: false, ParseFailed: true, []);
        }
    }

    private static ApprovalPreview ParseCore(string tool, string exact, bool isFullArgs)
    {
        var jsonStart = isFullArgs ? 0 : exact.IndexOf('{', StringComparison.Ordinal);
        if (jsonStart < 0)
        {
            return Degraded(exact, truncated: false);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(exact[jsonStart..]);
            root = document.RootElement.Clone();
        }
        catch (JsonException) when (!isFullArgs)
        {
            // The server cut its preview mid-JSON: what follows is not an argument list we can trust.
            return Degraded(exact, truncated: true);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Degraded(exact, truncated: false);
        }

        var args = new List<KeyValuePair<string, string>>();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
            args.Add(new KeyValuePair<string, string>(property.Name, value));
            if (!byName.TryAdd(property.Name, value))
            {
                conflicts.Add(property.Name);
            }
        }

        if (tool.Contains("mail", StringComparison.OrdinalIgnoreCase) && EmailKeys.Any(byName.ContainsKey))
        {
            var rest = args.Where(a => !EmailKeys.Contains(a.Key, StringComparer.OrdinalIgnoreCase) && !a.Key.Equals("cc", StringComparison.OrdinalIgnoreCase)).ToList();
            return new ApprovalPreview("email", ToEmail(byName), null, rest, exact, Truncated: false, ParseFailed: false, conflicts);
        }

        var diff = args.FirstOrDefault(a => a.Key.Equals("diff", StringComparison.OrdinalIgnoreCase) || a.Key.Equals("patch", StringComparison.OrdinalIgnoreCase) || LooksLikeUnifiedDiff(a.Value));
        return diff.Key is not null
            ? new ApprovalPreview("diff", null, diff.Value, [.. args.Where(a => a.Key != diff.Key)], exact, Truncated: false, ParseFailed: false, conflicts)
            : new ApprovalPreview("args", null, null, args, exact, Truncated: false, ParseFailed: false, conflicts);
    }

    private static ApprovalPreview Degraded(string exact, bool truncated) =>
        new("args", null, null, [], exact, truncated, ParseFailed: !truncated, []);

    private static bool LooksLikeUnifiedDiff(string value) =>
        value.StartsWith("diff --git", StringComparison.Ordinal) || value.StartsWith("--- ", StringComparison.Ordinal) || value.Contains("\n@@ ", StringComparison.Ordinal);

    private static EmailPreview ToEmail(Dictionary<string, string> args)
    {
        var (text, references, hidden, images, links) = Flatten(args.GetValueOrDefault("body") ?? string.Empty);
        return new EmailPreview(args.GetValueOrDefault("to") ?? string.Empty, args.GetValueOrDefault("cc"), args.GetValueOrDefault("subject") ?? string.Empty, text, images, links, references, hidden);
    }

    /// <summary>
    /// Strips every complete tag and decodes entities. Text is never dropped: an unterminated <c>&lt;x</c> stays as text,
    /// script/style/comment content is returned separately instead of vanishing, and URL attributes are listed.
    /// </summary>
    internal static (string Text, IReadOnlyList<string> References, IReadOnlyList<string> Hidden, int Images, int Links) Flatten(string body)
    {
        if (!body.Contains('<', StringComparison.Ordinal))
        {
            return (body, [], [], 0, 0);
        }

        var hidden = new List<string>();
        var text = HiddenBlock().Replace(body, match =>
        {
            hidden.Add(WebUtility.HtmlDecode(match.Groups["content"].Value.Trim()));
            return " ";
        });
        text = Comment().Replace(text, match =>
        {
            hidden.Add(WebUtility.HtmlDecode(match.Groups["content"].Value.Trim()));
            return " ";
        });

        var references = new List<string>();
        int images = 0, links = 0;
        foreach (Match tag in AnyTag().Matches(text))
        {
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var closing = tag.Value.StartsWith("</", StringComparison.Ordinal);
            images += !closing && name == "img" ? 1 : 0;
            links += !closing && name == "a" ? 1 : 0;
            foreach (Match attribute in UrlAttribute().Matches(tag.Value))
            {
                references.Add($"{name} {attribute.Groups["attr"].Value.ToLowerInvariant()}: {WebUtility.HtmlDecode(Unquote(attribute.Groups["value"].Value))}");
            }

            foreach (Match css in CssUrl().Matches(tag.Value))
            {
                references.Add($"{name} css url: {WebUtility.HtmlDecode(css.Groups["value"].Value.Trim())}");
            }
        }

        text = LineBreak().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        return (WebUtility.HtmlDecode(text).Trim(), references, hidden, images, links);
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] is '"' or '\'' ? value[1..^1] : value;

    [GeneratedRegex(@"<(?<tag>script|style)\b[^>]*>(?<content>.*?)</\k<tag>\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 250)]
    private static partial Regex HiddenBlock();

    [GeneratedRegex(@"<!--(?<content>.*?)-->", RegexOptions.Singleline, 250)]
    private static partial Regex Comment();

    // Only complete tags: an unterminated "<x" is text, not a tag, so nothing after it can be swallowed.
    [GeneratedRegex(@"</?(?<name>[a-zA-Z][a-zA-Z0-9:-]*)\b[^<>]*>", RegexOptions.None, 250)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\b(?<attr>href|src|srcset|action|formaction|background|poster|data|cite|xlink:href)\s*=\s*(?<value>""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex UrlAttribute();

    [GeneratedRegex(@"url\(\s*(?<value>[^)]*)\)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"<br\s*/?>|</p\s*>|</div\s*>|</li\s*>|</h[1-6]\s*>", RegexOptions.IgnoreCase, 250)]
    private static partial Regex LineBreak();
}
