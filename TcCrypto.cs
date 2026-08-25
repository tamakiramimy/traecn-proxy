using System.Security.Cryptography;
using System.Text;

namespace TrancnProxy;

/// <summary>
/// Trae CN "tc" 加密格式(与 laojichao/trae-local-api 的 trae-decrypt.js 等价)。
/// 结构: base64([6B header][32B random][AES-128-CBC([64B SHA-512 hash][plaintext])])
/// 密钥派生: SHA-512(random) -> XOR salt -> SHA-512 -> key[0..16] + iv[16..32]
/// </summary>
public static class TcCrypto
{
    private static readonly byte[] SaltA =
    {
        82,9,106,213,48,54,165,56,191,64,163,158,129,243,215,251,
        124,227,57,130,155,47,255,135,52,142,67,68,196,222,233,203,
        84,123,148,50,166,194,35,61,238,76,149,11,66,250,195,78,
        8,46,161,102,40,217,36,178,118,91,162,73,109,139,209,37
    };

    private static readonly byte[] SaltB =
    {
        31,221,168,51,136,7,199,49,177,18,16,89,39,128,236,95,
        96,81,127,169,25,181,74,13,45,229,122,159,147,201,156,239,
        160,224,59,77,174,42,245,176,200,235,187,60,131,83,153,97,
        23,43,4,126,186,119,214,38,225,105,20,99,85,33,12,125
    };

    private static readonly byte[] SaltC =
    {
        191,192,216,250,122,246,220,97,31,254,98,27,8,72,71,176,
        135,99,96,18,127,101,203,104,211,102,191,125,37,72,150,156,
        51,229,121,35,17,153,141,177,110,131,150,128,172,255,254,6,
        18,140,55,62,236,249,135,64,135,12,117,4,89,149,168,209
    };

    private static readonly byte[] SaltD =
    {
        246,204,26,232,232,70,129,109,223,146,169,242,23,241,105,145,
        50,196,165,42,254,120,3,54,244,207,209,85,53,6,138,106,
        175,148,31,204,186,186,165,182,87,142,49,10,39,110,26,154,
        86,56,173,125,18,64,198,225,99,99,83,82,191,134,76,170
    };

    private static readonly byte[] HeaderAes = { 0x74, 0x63, 0x05, 0x10, 0x00, 0x00 };      // "tc" AES
    private static readonly byte[] HeaderAesPrivate = { 18, 57, 32, 32, 2, 3 };              // AES_PRIVATE

    public static string DecryptStorageValue(string base64Value)
    {
        byte[] buffer = Convert.FromBase64String(base64Value);
        byte[] header = buffer[..6];
        byte[] randomBytes = buffer[6..38];
        byte[] encrypted = buffer[38..];

        (byte[] key, byte[] iv) = DeriveKeyAndIv(randomBytes, header);
        byte[] decrypted = AesDecrypt(key, iv, encrypted);

        byte[] storedHash = decrypted[..64];
        byte[] plaintext = decrypted[64..];
        byte[] computedHash = SHA512.HashData(plaintext);
        if (!storedHash.AsSpan().SequenceEqual(computedHash))
            throw new InvalidDataException("tc 解密校验失败(hash mismatch)");

        return Encoding.UTF8.GetString(plaintext);
    }

    public static string EncryptStorageValue(string plaintext)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] hash = SHA512.HashData(plainBytes);
        byte[] payload = hash.Concat(plainBytes).ToArray();

        byte[] randomBytes = RandomNumberGenerator.GetBytes(32);
        (byte[] key, byte[] iv) = DeriveKeyAndIv(randomBytes, HeaderAes);
        byte[] encrypted = AesEncrypt(key, iv, payload);

        byte[] result = HeaderAes.Concat(randomBytes).Concat(encrypted).ToArray();
        return Convert.ToBase64String(result);
    }

    private static (byte[] key, byte[] iv) DeriveKeyAndIv(byte[] randomBytes, byte[] header)
    {
        bool isPrivate = header.Length >= 6 && header[0] == 18 && header[1] == 57;
        byte[] salt = Xor(isPrivate ? SaltC : SaltA, isPrivate ? SaltD : SaltB);

        byte[] hashOfRandom = SHA512.HashData(randomBytes);
        byte[] finalHash = SHA512.HashData(hashOfRandom.Concat(salt).ToArray());

        return (finalHash[..16], finalHash[16..32]);
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    private static byte[] AesDecrypt(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor(key, iv);
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEncrypt(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor(key, iv);
        return enc.TransformFinalBlock(data, 0, data.Length);
    }
}
