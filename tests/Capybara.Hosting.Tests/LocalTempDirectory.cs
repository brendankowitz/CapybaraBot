namespace Capybara.Hosting.Tests;

internal sealed class LocalTempDirectory : IDisposable
{
    public LocalTempDirectory()
    {
        Path = System.IO.Path.Combine(Directory.GetCurrentDirectory(), ".capybara-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
