namespace Capybara.Gateway.Composition;

/// <summary>
/// Configuration problems that do not stop capybarad but degrade it. Composition runs before logging exists, so they
/// are collected here and logged once the host is built.
/// </summary>
public sealed class CompositionWarnings
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    internal void Add(string message) => _messages.Add(message);
}
