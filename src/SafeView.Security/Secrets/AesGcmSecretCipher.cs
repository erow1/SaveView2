using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Security;
using SafeView.Application.Abstractions.Storage;

namespace SafeView.Security.Secrets;

/// <summary>
/// AES-GCM 256-bit implementacja <see cref="ISecretCipher"/>.
///
/// Klucz persistowany w pliku <c>storage/licenses/secrets.key</c> (32 bajty, binary).
/// Przy pierwszym uruchomieniu klucz generowany <see cref="RandomNumberGenerator"/>.
/// Format ciphertext: <c>"enc:v1:{base64(12-byte-nonce || ciphertext || 16-byte-tag)}"</c>.
/// </summary>
public sealed class AesGcmSecretCipher : ISecretCipher, IDisposable
{
    private const string Prefix = "enc:v1:";
    private const int KeyLen = 32;
    private const int NonceLen = 12;
    private const int TagLen = 16;

    private readonly byte[] _key;
    private readonly AesGcm _aes;

    public AesGcmSecretCipher(IFileStore files, ILogger<AesGcmSecretCipher> log)
    {
        // Klucz w storage/licenses/secrets.key (istniejący katalog dla sekretów)
        var keyPath = files.ResolveAbsolutePath(FileKind.License, "secrets.key");
        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
            if (_key.Length != KeyLen)
                throw new InvalidOperationException($"Invalid secret key length at {keyPath} (expected {KeyLen}, got {_key.Length}).");
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(KeyLen);
            File.WriteAllBytes(keyPath, _key);
            // Unix file permissions 600 (tylko owner)
            try { File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* ignore on Windows */ }
            log.LogWarning("Generated new secret cipher key at {Path}. BACKUP THIS FILE — rotation not yet supported.", keyPath);
        }

        _aes = new AesGcm(_key, TagLen);
    }

    public bool IsEncrypted(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (IsEncrypted(plaintext)) return plaintext; // idempotent

        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];
        _aes.Encrypt(nonce, plain, cipher, tag);

        // Format: nonce || cipher || tag  (all concatenated, base64'd)
        var combined = new byte[NonceLen + cipher.Length + TagLen];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceLen);
        Buffer.BlockCopy(cipher, 0, combined, NonceLen, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceLen + cipher.Length, TagLen);

        return Prefix + Convert.ToBase64String(combined);
    }

    public string Decrypt(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!IsEncrypted(value)) return value; // legacy plaintext — compat

        try
        {
            var b64 = value[Prefix.Length..];
            var combined = Convert.FromBase64String(b64);
            if (combined.Length < NonceLen + TagLen)
                throw new FormatException("Ciphertext too short");

            var nonce = new byte[NonceLen];
            var tag = new byte[TagLen];
            var cipher = new byte[combined.Length - NonceLen - TagLen];
            Buffer.BlockCopy(combined, 0, nonce, 0, NonceLen);
            Buffer.BlockCopy(combined, NonceLen, cipher, 0, cipher.Length);
            Buffer.BlockCopy(combined, NonceLen + cipher.Length, tag, 0, TagLen);

            var plain = new byte[cipher.Length];
            _aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Wrong key / tampered data → best effort: return sentinel (NIE plaintext oryginał)
            // Caller sprawdzi i obsłuży (np. loguje + disable action)
            throw new InvalidOperationException("Failed to decrypt secret — key rotated or data tampered.");
        }
    }

    public void Dispose() => _aes.Dispose();
}
