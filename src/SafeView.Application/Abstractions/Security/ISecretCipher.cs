namespace SafeView.Application.Abstractions.Security;

/// <summary>
/// Szyfrowanie sekretów (SMTP hasła, webhook tokeny) w <c>Action.Config</c>.
/// Format ciphertext: <c>"enc:v1:{base64(nonce + ciphertext + tag)}"</c>.
/// Backward compat: gdy wartość nie ma prefiksu <c>enc:v1:</c> traktuj jako plaintext
/// (migracja przy pierwszym zapisie).
/// </summary>
public interface ISecretCipher
{
    /// <summary>Szyfruje plaintext → format enc:v1:... (deterministic prefix).</summary>
    string Encrypt(string plaintext);

    /// <summary>Deszyfruje wartość. Gdy input nie ma prefiksu enc:v1: — zwraca jak jest (plaintext legacy).</summary>
    string Decrypt(string value);

    /// <summary>Czy wartość jest już zaszyfrowana (ma prefix enc:v1:).</summary>
    bool IsEncrypted(string value);
}

/// <summary>
/// Polityka — które klucze w Action.Config są sekretami (szyfrowane).
/// Hardcoded żeby nie rozjechały się per deployment.
/// </summary>
public static class SecretsPolicy
{
    public static readonly HashSet<string> SensitiveConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",       // SMTP
        "secret",         // Webhook Bearer
        "token",          // Generic API token
        "api_key",        // Generic API key
        "authToken",      // Twilio (ticket #4)
        "accountSid",     // Twilio — SID też wrażliwy
    };

    public static bool IsSensitive(string key) => SensitiveConfigKeys.Contains(key);
}
