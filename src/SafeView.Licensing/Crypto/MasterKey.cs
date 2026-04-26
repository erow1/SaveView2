using System.Security.Cryptography;
using System.Text;

namespace SafeView.Licensing.Crypto;

/// <summary>
/// Master key zaszyty w kodzie, składany z fragmentów + stałą solą.
/// CELOWO obfuskowany, nie szyfrowany — to bariera przed "casual tamperingiem", nie reverse-engineeringiem.
/// Rzeczywistym zabezpieczeniem przed podrobieniem licencji jest HMAC i fakt, że klient nie ma dostępu do tej stałej.
/// ZMIANA TEJ STAŁEJ UNIEWAŻNI WSZYSTKIE ISTNIEJĄCE LICENCJE.
/// </summary>
internal static class MasterKey
{
    // Fragmenty — ukryte w kilku miejscach; finalny klucz = concat + PBKDF2.
    private static readonly string[] Fragments =
    [
        "sv-", "9K2p", "_x7!", "QrZ#", "6m$B", "nE^4", "LvA*", "t1uY"
    ];

    private static readonly byte[] Salt =
        Encoding.UTF8.GetBytes("SafeView-License-Salt-2026-DoNotChange");

    private const int DerivedKeyBytes = 64; // 32 dla AES-256, 32 dla HMAC-SHA256
    private const int Pbkdf2Iterations = 100_000;

    /// <summary>Zwraca 64 bajty: pierwsze 32 = klucz AES-256, ostatnie 32 = klucz HMAC-SHA256.</summary>
    public static byte[] Derive()
    {
        var password = string.Concat(Fragments);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            DerivedKeyBytes);
    }

    public static (byte[] aesKey, byte[] hmacKey) DeriveSplit()
    {
        var all = Derive();
        var aes = new byte[32];
        var hmac = new byte[32];
        Array.Copy(all, 0, aes, 0, 32);
        Array.Copy(all, 32, hmac, 0, 32);
        return (aes, hmac);
    }
}
