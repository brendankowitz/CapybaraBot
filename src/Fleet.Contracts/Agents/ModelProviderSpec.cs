namespace Fleet.Contracts.Agents;

/// <summary>
/// Optional bring-your-own-key provider for high-volume background agents (FR-AGT-10, NFR-03), e.g. Azure OpenAI,
/// Foundry or Ollama. The key is read from the secret store on every create and resume and never written to config.
/// </summary>
public sealed record ModelProviderSpec
{
    /// <summary>SDK provider type, e.g. <c>openai</c>, <c>azure</c> or <c>ollama</c>.</summary>
    public required string Type { get; init; }

    public required string BaseUrl { get; init; }

    public string? WireApi { get; init; }

    /// <summary>Secret-store name holding the API key. Optional for keyless local providers such as Ollama.</summary>
    public string? ApiKeySecret { get; init; }

    public string? ModelId { get; init; }

    public string? AzureApiVersion { get; init; }
}
