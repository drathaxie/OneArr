using System.Security.Cryptography;
using System.Text;

namespace OneArr.Infrastructure.Security;

/// <summary>PBKDF2 password hashing. Format: pbkdf2-sha256$iterations$saltB64$hashB64.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Stateless HMAC bearer tokens. The signing key lives in the config directory with owner-only permissions.</summary>
public sealed class TokenService
{
    private readonly byte[] _key;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public TokenService(byte[] key)
    {
        _key = key;
    }

    public static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path)) return Convert.FromBase64String(File.ReadAllText(path).Trim());
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Convert.ToBase64String(key) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(DateTimeOffset now)
    {
        var expires = now.Add(Lifetime);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(expires.ToUnixTimeSeconds() + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(12))));
        return ($"{payload}.{Sign(payload)}", expires);
    }

    public bool Validate(string token, DateTimeOffset now)
    {
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return false;
        var payload = token[..dot];
        var sig = token[(dot + 1)..];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sig), Encoding.UTF8.GetBytes(Sign(payload)))) return false;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return long.TryParse(decoded.Split('.')[0], out var exp) && exp > now.ToUnixTimeSeconds();
    }

    private string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
}
