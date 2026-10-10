using Fleet.Contracts.Messaging;

namespace Fleet.Memory.Abstractions;

/// <summary>One recalled memory item from a pluggable backend; provider text is treated as data and scanned (FR-MEM-10, FR-SAF-03).</summary>
public sealed record MemoryRecall(string Path, string Description, string Text, DateTimeOffset Updated, Taint Taint = Taint.Tainted);
