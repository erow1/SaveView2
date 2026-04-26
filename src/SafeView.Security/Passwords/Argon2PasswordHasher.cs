using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace SafeView.Security.Passwords;

/// <summary>
/// Argon2id password hasher. Format hash (single string, base64-encoded):
///   v=1|m=65536|t=3|p=2|salt|hash
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string storedHash);
}

public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int Version = 1;
    private const int MemoryKib = 64 * 1024;      // 64 MB
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeHash(password, salt, MemoryKib, Iterations, Parallelism, HashSize);
        return $"v={Version}|m={MemoryKib}|t={Iterations}|p={Parallelism}|" +
               $"{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash)) return false;
        try
        {
            var parts = storedHash.Split('|');
            if (parts.Length != 6) return false;
            int m = int.Parse(parts[1].AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
            int t = int.Parse(parts[2].AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
            int p = int.Parse(parts[3].AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
            var salt = Convert.FromBase64String(parts[4]);
            var expected = Convert.FromBase64String(parts[5]);
            var actual = ComputeHash(password, salt, m, t, p, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int hashSize)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon2.GetBytes(hashSize);
    }
}
