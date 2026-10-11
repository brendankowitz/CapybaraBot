namespace Capybara.Memory.Pages;

/// <summary>A page's frontmatter is malformed or hostile (too deeply nested) and cannot be trusted.</summary>
internal sealed class InvalidFrontmatterException(string message, Exception? innerException = null) : Exception(message, innerException);
