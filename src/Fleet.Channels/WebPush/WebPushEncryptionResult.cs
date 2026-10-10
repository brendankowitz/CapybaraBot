namespace Fleet.Channels.WebPush;

internal sealed record WebPushEncryptionResult(byte[] Salt, byte[] ServerPublicKey, byte[] Ciphertext)
{
    public byte[] ToAes128GcmBody()
    {
        var body = new byte[16 + 4 + 1 + ServerPublicKey.Length + Ciphertext.Length];
        Salt.CopyTo(body, 0);
        body[16] = 0;
        body[17] = 0;
        body[18] = 16;
        body[19] = 0;
        body[20] = (byte)ServerPublicKey.Length;
        ServerPublicKey.CopyTo(body, 21);
        Ciphertext.CopyTo(body, 21 + ServerPublicKey.Length);
        return body;
    }

    public static WebPushEncryptionResult FromAes128GcmBody(byte[] body)
    {
        var salt = body[..16];
        var keyLength = body[20];
        var key = body[21..(21 + keyLength)];
        var ciphertext = body[(21 + keyLength)..];
        return new WebPushEncryptionResult(salt, key, ciphertext);
    }
}
