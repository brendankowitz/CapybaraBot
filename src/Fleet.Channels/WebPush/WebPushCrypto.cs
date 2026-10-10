using System.Security.Cryptography;
using System.Text;

namespace Fleet.Channels.WebPush;

internal static class WebPushCrypto
{
    public static WebPushEncryptionResult Encrypt(ReadOnlySpan<byte> payload, string receiverPublicKey, string authSecret)
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var receiver = ImportEcdhPublic(Base64UrlDecode(receiverPublicKey));
        var sharedSecret = server.DeriveRawSecretAgreement(receiver.PublicKey);
        var serverPublicKey = ExportUncompressed(server.ExportParameters(false));
        var receiverPublicKeyBytes = Base64UrlDecode(receiverPublicKey);
        var keys = DeriveContentKeys(sharedSecret, Base64UrlDecode(authSecret), RandomNumberGenerator.GetBytes(16), receiverPublicKeyBytes, serverPublicKey);
        var plaintext = new byte[payload.Length + 1];
        payload.CopyTo(plaintext);
        plaintext[^1] = 2;
        var ciphertext = new byte[plaintext.Length + 16];
        using var aes = new AesGcm(keys.ContentEncryptionKey, 16);
        aes.Encrypt(keys.Nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length, 16));
        return new WebPushEncryptionResult(keys.Salt, serverPublicKey, ciphertext);
    }

    public static byte[] Decrypt(WebPushEncryptionResult encrypted, ECDiffieHellman receiverPrivateKey, string authSecret, string receiverPublicKey)
    {
        using var server = ImportEcdhPublic(encrypted.ServerPublicKey);
        var sharedSecret = receiverPrivateKey.DeriveRawSecretAgreement(server.PublicKey);
        var keys = DeriveContentKeys(sharedSecret, Base64UrlDecode(authSecret), encrypted.Salt, Base64UrlDecode(receiverPublicKey), encrypted.ServerPublicKey);
        var plaintext = new byte[encrypted.Ciphertext.Length - 16];
        using var aes = new AesGcm(keys.ContentEncryptionKey, 16);
        aes.Decrypt(keys.Nonce, encrypted.Ciphertext.AsSpan(0, plaintext.Length), encrypted.Ciphertext.AsSpan(plaintext.Length, 16), plaintext);
        return plaintext[^1] == 2 ? plaintext[..^1] : plaintext;
    }

    public static string CreateVapidJwt(Uri endpoint, WebPushVapidOptions options, TimeProvider timeProvider)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var audience = $"{endpoint.Scheme}://{endpoint.Host}";
        if (!endpoint.IsDefaultPort)
        {
            audience += $":{endpoint.Port}";
        }

        var exp = timeProvider.GetUtcNow().AddHours(12).ToUnixTimeSeconds();
        var payloadJson = $"{{\"aud\":\"{audience}\",\"exp\":{exp},\"sub\":\"{options.Subject}\"}}";
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var unsigned = $"{header}.{payload}";
        using var ecdsa = ImportEcdsaPrivate(Base64UrlDecode(options.PrivateKey));
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{unsigned}.{Base64UrlEncode(signature)}";
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> value)
    {
        var encoded = Convert.ToBase64String(value);
        var trimmed = encoded[..(encoded.LastIndexOfAnyExcept('=') + 1)];
        return trimmed.Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    public static byte[] ExportUncompressed(ECParameters parameters)
    {
        var key = new byte[65];
        key[0] = 4;
        parameters.Q.X!.CopyTo(key, 1);
        parameters.Q.Y!.CopyTo(key, 33);
        return key;
    }

    private static (byte[] Salt, byte[] ContentEncryptionKey, byte[] Nonce) DeriveContentKeys(byte[] sharedSecret, byte[] authSecret, byte[] salt, byte[] receiverPublicKey, byte[] serverPublicKey)
    {
        var prkKey = HkdfExtract(authSecret, sharedSecret);
        var keyInfo = Combine(Encoding.ASCII.GetBytes("WebPush: info\0"), receiverPublicKey, serverPublicKey);
        var ikm = HkdfExpand(prkKey, keyInfo, 32);
        var prk = HkdfExtract(salt, ikm);
        return (salt, HkdfExpand(prk, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"), 16), HkdfExpand(prk, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), 12));
    }

    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        using var hmac = new HMACSHA256(prk);
        var previous = Array.Empty<byte>();
        var output = new List<byte>(length);
        byte counter = 1;
        while (output.Count < length)
        {
            var blockInput = Combine(previous, info, [counter]);
            previous = hmac.ComputeHash(blockInput);
            output.AddRange(previous);
            counter++;
        }

        return [.. output.Take(length)];
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        var length = arrays.Sum(a => a.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var array in arrays)
        {
            array.CopyTo(result, offset);
            offset += array.Length;
        }

        return result;
    }

    private static ECDiffieHellman ImportEcdhPublic(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 4)
        {
            throw new CryptographicException("Expected uncompressed P-256 public key.");
        }

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] },
        });
    }

    private static ECDsa ImportEcdsaPrivate(byte[] privateKey)
    {
        if (privateKey.Length == 32)
        {
            return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateKey });
        }

        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey[..32],
            Q = new ECPoint { X = privateKey[32..64], Y = privateKey[64..96] },
        });
    }
}
