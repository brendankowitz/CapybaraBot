using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Capybara.Contracts.Storage;

namespace Capybara.Connections.Vault;

/// <summary>
/// Writable broker-local secret store, encrypted at rest with AES-256-GCM (FR-CON-04). The key comes from a
/// <c>systemd-creds</c> credential (TPM-bound on the host), so refreshed OAuth tokens persist across restarts without
/// ever being written in plaintext. Each file is bound to its secret name as associated data, so ciphertexts cannot be
/// swapped between names; a tampered file fails decryption loudly instead of reading as absent.
/// </summary>
/// <remarks>Thread-safe: writes go to a temporary file and are renamed into place atomically.</remarks>
public sealed partial class EncryptedFileSecretStore : ISecretStore
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string Extension = ".secret";

    private readonly string _directory;
    private readonly byte[] _key;

    /// <exception cref="ArgumentException">The key is not 32 bytes.</exception>
    public EncryptedFileSecretStore(string directory, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (key.Length != 32)
        {
            throw new ArgumentException("The vault key must be 32 bytes (AES-256).", nameof(key));
        }

        _directory = directory;
        _key = key.ToArray();
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Reads a base64 32-byte key, typically <c>$CREDENTIALS_DIRECTORY/broker-vault-key</c>.</summary>
    /// <exception cref="InvalidOperationException">The key file is missing or malformed.</exception>
    public static byte[] LoadKey(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Vault key '{path}' is missing. Provision it with systemd-creds (32 random bytes, base64).");
        }

        try
        {
            var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
            return key.Length == 32 ? key : throw new InvalidOperationException($"Vault key '{path}' must decode to 32 bytes.");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Vault key '{path}' is not base64.", ex);
        }
    }

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var blob = await File.ReadAllBytesAsync(path, cancellationToken);
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != FormatVersion)
        {
            throw new CryptographicException($"Secret '{name}' has an unknown format.");
        }

        var nonce = blob.AsSpan(1, NonceSize);
        var tag = blob.AsSpan(1 + NonceSize, TagSize);
        var cipher = blob.AsSpan(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(name));
        return Encoding.UTF8.GetString(plain);
    }

    public async ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(name);
        var plain = Encoding.UTF8.GetBytes(value);
        var blob = new byte[1 + NonceSize + TagSize + plain.Length];
        blob[0] = FormatVersion;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plain, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), Encoding.UTF8.GetBytes(name));
        }

        var temp = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var stream = new FileStream(temp, options))
        {
            await stream.WriteAsync(blob, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        File.Delete(PathFor(name));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([.. Directory.EnumerateFiles(_directory, "*" + Extension)
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(SecretNames.IsValid)
            .Order(StringComparer.Ordinal)]);

    private string PathFor(string name) => Path.Combine(_directory, SecretNames.Require(name) + Extension);
}

/// <summary>Flat secret names compatible with <c>systemd-creds</c> credential names.</summary>
public static partial class SecretNames
{
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    /// <exception cref="ArgumentException">The name is not a valid flat secret name.</exception>
    public static string Require(string name) =>
        IsValid(name) ? name : throw new ArgumentException($"Secret name '{name}' must match [a-z0-9][a-z0-9._-]*.", nameof(name));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
