using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SafeView.Licensing.Model;

namespace SafeView.Licensing.Crypto;

/// <summary>
/// Format pliku .lic:
///   [12B nonce][32B HMAC-SHA256(nonce||ciphertext)][N B ciphertext||16B AES-GCM tag]
/// Zserializowany jako Base64 wewnątrz pliku tekstowego z nagłówkiem "SAFEVIEW-LIC-V1\n".
/// </summary>
public sealed class LicenseCryptoService
{
    private const string Header = "SAFEVIEW-LIC-V1\n";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HmacSize = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Encrypt(LicenseFile license)
    {
        ArgumentNullException.ThrowIfNull(license);
        var (aesKey, hmacKey) = MasterKey.DeriveSplit();

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(license, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(aesKey, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // HMAC nad nonce || ciphertext || tag
        byte[] mac;
        using (var hmac = new HMACSHA256(hmacKey))
        {
            var macInput = new byte[nonce.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, macInput, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, macInput, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, macInput, nonce.Length + ciphertext.Length, tag.Length);
            mac = hmac.ComputeHash(macInput);
        }

        var blob = new byte[NonceSize + HmacSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(mac, 0, blob, NonceSize, HmacSize);
        Buffer.BlockCopy(ciphertext, 0, blob, NonceSize + HmacSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceSize + HmacSize + ciphertext.Length, TagSize);

        return Header + Convert.ToBase64String(blob);
    }

    public LicenseFile Decrypt(string licenseText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseText);
        if (!licenseText.StartsWith(Header, StringComparison.Ordinal))
            throw new InvalidLicenseException("Nieprawidłowy nagłówek pliku licencji.");

        var base64 = licenseText[Header.Length..].Trim();
        byte[] blob;
        try { blob = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new InvalidLicenseException("Zawartość licencji nie jest prawidłowym Base64."); }

        if (blob.Length < NonceSize + HmacSize + TagSize + 1)
            throw new InvalidLicenseException("Plik licencji uszkodzony (zbyt krótki).");

        var nonce = blob[..NonceSize];
        var mac = new byte[HmacSize];
        Buffer.BlockCopy(blob, NonceSize, mac, 0, HmacSize);
        var ciphertextLen = blob.Length - NonceSize - HmacSize - TagSize;
        var ciphertext = new byte[ciphertextLen];
        Buffer.BlockCopy(blob, NonceSize + HmacSize, ciphertext, 0, ciphertextLen);
        var tag = new byte[TagSize];
        Buffer.BlockCopy(blob, NonceSize + HmacSize + ciphertextLen, tag, 0, TagSize);

        var (aesKey, hmacKey) = MasterKey.DeriveSplit();

        // Weryfikacja HMAC
        using (var hmac = new HMACSHA256(hmacKey))
        {
            var macInput = new byte[nonce.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, macInput, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, macInput, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, macInput, nonce.Length + ciphertext.Length, tag.Length);
            var expected = hmac.ComputeHash(macInput);
            if (!CryptographicOperations.FixedTimeEquals(expected, mac))
                throw new InvalidLicenseException("HMAC niepoprawny — plik licencji zmodyfikowany lub niezgodny z kluczem.");
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(aesKey, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidLicenseException("AES-GCM: deszyfrowanie nie powiodło się.", ex);
        }

        var license = JsonSerializer.Deserialize<LicenseFile>(plaintext, JsonOptions)
            ?? throw new InvalidLicenseException("Nie udało się zdeserializować zawartości licencji.");

        return license;
    }

    public static string ComputeFingerprint(string licenseText)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(licenseText));
        return Convert.ToHexString(hash)[..16];
    }
}

public sealed class InvalidLicenseException : Exception
{
    public InvalidLicenseException(string message) : base(message) { }
    public InvalidLicenseException(string message, Exception inner) : base(message, inner) { }
}
