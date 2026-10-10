using System.Text;
using Fleet.Hosting.Storage;

namespace Fleet.Hosting.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task Sqlite_state_store_supports_crud_and_prefix()
    {
        using var temp = new LocalTempDirectory();
        var store = new SqliteStateStore(Path.Combine(temp.Path, "state.db"));
        var ct = TestContext.Current.CancellationToken;

        await store.PutAsync("agents", "ea/one", """{"n":1}""", ct);
        await store.PutAsync("agents", "code/two", """{"n":2}""", ct);

        Assert.Equal("""{"n":1}""", await store.GetAsync("agents", "ea/one", ct));
        var prefixed = await store.ListAsync("agents", "ea/", ct);
        Assert.Single(prefixed);
        Assert.True(await store.DeleteAsync("agents", "ea/one", ct));
        Assert.Null(await store.GetAsync("agents", "ea/one", ct));
    }

    [Fact]
    public async Task File_system_blob_store_rejects_traversal_and_roundtrips()
    {
        using var temp = new LocalTempDirectory();
        var store = new FileSystemBlobStore(temp.Path);
        var ct = TestContext.Current.CancellationToken;
        await store.WriteAsync("safe/blob.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")), ct);

        await using var read = await store.OpenReadAsync("safe/blob.txt", ct);
        using var reader = new StreamReader(read!);
        Assert.Equal("hello", await reader.ReadToEndAsync(ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.WriteAsync("../escape", new MemoryStream(), ct));
    }
}
