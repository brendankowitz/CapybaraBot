using System.Security.Cryptography;
using System.Text;
using Capybara.Connections.Vault;
using Capybara.Testing;

namespace Capybara.Connections.Tests;

public sealed class VaultTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task Encrypted_store_round_trips_without_plaintext_on_disk()
    {
        using var dir = new TempDirectory();
        var store = new EncryptedFileSecretStore(dir.Path, Key);
        var ct = TestContext.Current.CancellationToken;

        await store.SetAsync("connection-email", "refresh-token-value", ct);
        await store.SetAsync("connection-email", "rotated-value", ct);

        Assert.Equal("rotated-value", await store.GetAsync("connection-email", ct));
        Assert.Equal(["connection-email"], await store.ListAsync(ct));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "connection-email.secret"), ct);
        Assert.DoesNotContain("rotated-value", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Null(await store.GetAsync("connection-missing", ct));
    }

    [Fact]
    public async Task Tampered_swapped_or_wrong_key_ciphertext_fails_loudly()
    {
        using var dir = new TempDirectory();
        var store = new EncryptedFileSecretStore(dir.Path, Key);
        var ct = TestContext.Current.CancellationToken;
        await store.SetAsync("connection-a", "secret-a", ct);
        await store.SetAsync("connection-b", "secret-b", ct);

        File.Copy(Path.Combine(dir.Path, "connection-a.secret"), Path.Combine(dir.Path, "connection-b.secret"), overwrite: true);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await store.GetAsync("connection-b", ct));

        var path = Path.Combine(dir.Path, "connection-a.secret");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await new EncryptedFileSecretStore(dir.Path, RandomNumberGenerator.GetBytes(32)).GetAsync("connection-a", ct));
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await store.GetAsync("connection-a", ct));
    }

    [Fact]
    public void Store_rejects_bad_keys_and_path_like_names()
    {
        using var dir = new TempDirectory();
        Assert.Throws<ArgumentException>(() => new EncryptedFileSecretStore(dir.Path, new byte[16]));
        var store = new EncryptedFileSecretStore(dir.Path, Key);
        Assert.Throws<ArgumentException>(() => store.SetAsync("../escape", "x", TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => store.SetAsync("connection/email", "x", TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void Vault_key_must_be_32_base64_bytes()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "broker-vault-key");

        Assert.Throws<InvalidOperationException>(() => EncryptedFileSecretStore.LoadKey(path));
        File.WriteAllText(path, Convert.ToBase64String(new byte[8]));
        Assert.Throws<InvalidOperationException>(() => EncryptedFileSecretStore.LoadKey(path));
        File.WriteAllText(path, Convert.ToBase64String(Key) + "\n");
        Assert.Equal(Key, EncryptedFileSecretStore.LoadKey(path));
    }

    [Fact]
    public async Task Refreshed_credentials_shadow_admin_seeded_ones()
    {
        using var seedDir = new TempDirectory();
        using var stateDir = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(seedDir.Path, "connection-github"), "github_pat_seeded\n", TestContext.Current.CancellationToken);
        var vault = new TokenVault(new LayeredSecretStore(new EncryptedFileSecretStore(stateDir.Path, Key), new CredentialsDirectorySecretStore(seedDir.Path)));
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("github_pat_seeded", (await vault.GetAsync("github", ct))!.AccessToken);
        await vault.SetAsync("github", new ConnectionCredential { AccessToken = "replacement", ExpiresAt = DateTimeOffset.UnixEpoch }, ct);

        var credential = await vault.GetAsync("github", ct);
        Assert.Equal(("replacement", DateTimeOffset.UnixEpoch), (credential!.AccessToken, credential.ExpiresAt));
        Assert.Equal(["github"], await vault.ListConnectionIdsAsync(ct));
        Assert.DoesNotContain("replacement", credential.ToString(), StringComparison.Ordinal);
    }
}
