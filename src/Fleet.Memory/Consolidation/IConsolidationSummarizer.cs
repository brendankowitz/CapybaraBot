namespace Fleet.Memory.Consolidation;

/// <summary>Optional hook for an LLM summarizer; deterministic consolidation works without one (FR-MEM-08).</summary>
public interface IConsolidationSummarizer
{
    ValueTask<string> SummarizeAsync(string content, CancellationToken cancellationToken = default);
}
