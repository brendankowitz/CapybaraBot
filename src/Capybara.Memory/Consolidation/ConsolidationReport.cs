namespace Capybara.Memory.Consolidation;

/// <summary>What one consolidation run staged for human review. Nothing in the wiki changes until a proposal is approved (FR-MEM-08).</summary>
/// <param name="PromotionsProposed">Notes pages that would gain bullets promoted from daily logs.</param>
/// <param name="DuplicatesProposed">Duplicate pages proposed for deletion.</param>
/// <param name="ArchivesProposed">Stale pages proposed for archiving.</param>
public sealed record ConsolidationReport(int PromotionsProposed, int DuplicatesProposed, int ArchivesProposed)
{
    public int Total => PromotionsProposed + DuplicatesProposed + ArchivesProposed;
}
