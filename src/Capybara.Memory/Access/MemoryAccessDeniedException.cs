namespace Capybara.Memory.Access;

public sealed class MemoryAccessDeniedException : Exception
{
    public MemoryAccessDeniedException()
    {
    }

    public MemoryAccessDeniedException(string message)
        : base(message)
    {
    }

    public MemoryAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
