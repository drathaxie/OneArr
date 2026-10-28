using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneArr.Application;

namespace OneArr.Infrastructure.Security;

/// <summary>
/// Authenticated encryption of native tracker credentials with a key derived
/// from OneArr's persistent config/secret.key. Never exposed in API responses.
/// This key MUST be retained across upgrades (the Windows installer already
/// preserves it); restoring only the database without secret.key is not enough.
/// </summary>
public sealed class TrackerSecretBox
{
    private const string Prefix = "native1:";
    private readonly byte[] _key;

    public TrackerSecretBox(byte[] masterKey)
    {
        _key = SHA256.HashData(masterKey.Concat(Encoding.UTF8.GetBytes("OneArr native tracker credentials v1")).ToArray());
    }

    public static bool IsNative(string? stored) =>
        stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public string Protect(NativeTrackerCredentials credentials)
    {
        if (!NativeTrackerCredentials.IsSupported(credentials.TrackerId))
            throw new ArgumentException("Unsupported native tracker");
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
            throw new ArgumentException("Tracker username and password are required");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials));
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16))
            aes.Encrypt(nonce, plain, ciphertext, tag, Encoding.UTF8.GetBytes(Prefix));
        CryptographicOperations.ZeroMemory(plain);
        return Prefix + Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }

    public NativeTrackerCredentials Read(string stored)
    {
        if (!IsNative(stored)) throw new ExternalServiceException("Not a native tracker credential record");
        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            if (bytes.Length < 29) throw new CryptographicException("Invalid ciphertext");
            var plain = new byte[bytes.Length - 28];
            using (var aes = new AesGcm(_key, 16))
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(Prefix));
            try
            {
                var config = JsonSerializer.Deserialize<NativeTrackerCredentials>(plain)
                    ?? throw new CryptographicException("Invalid tracker record");
                if (!NativeTrackerCredentials.IsSupported(config.TrackerId))
                    throw new CryptographicException("Unknown native tracker");
                return config;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            throw new ExternalServiceException("Native tracker credentials could not be decrypted. Check that config/secret.key was preserved.");
        }
    }
}

public sealed record NativeTrackerCredentials(string TrackerId, string Username, string Password, string Alt2FaToken = "")
{
    public static bool IsSupported(string? id) => string.Equals(id, "torrentleech", StringComparison.Ordinal);
}
