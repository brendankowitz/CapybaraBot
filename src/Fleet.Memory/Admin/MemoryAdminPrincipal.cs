namespace Fleet.Memory.Admin;

/// <summary>Principal kind for PWA memory administration; agents are rejected for human-only actions (FR-MEM-12).</summary>
public enum MemoryAdminPrincipal
{
    Agent,
    Human,
}
