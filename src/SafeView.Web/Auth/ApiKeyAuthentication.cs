using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Security.Auth;

namespace SafeView.Web.Auth;

public static class ApiKeyAuth
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-API-Key";

    /// <summary>SHA-256 hex (lowercase) — używane jako KeyHash w bazie.</summary>
    public static string Hash(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Generuje nowy klucz: "sv_" + 40 znaków base64url (~30 bajtów entropii).</summary>
    public static string Generate()
    {
        Span<byte> buf = stackalloc byte[30];
        RandomNumberGenerator.Fill(buf);
        var b64 = Convert.ToBase64String(buf)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return "sv_" + b64;
    }
}

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    private readonly IApiKeyRepository _repo;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyRepository repo) : base(options, logger, encoder)
    {
        _repo = repo;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuth.HeaderName, out var values))
            return AuthenticateResult.NoResult();

        var raw = values.ToString().Trim();
        if (string.IsNullOrEmpty(raw))
            return AuthenticateResult.Fail("Empty API key.");

        var hash = ApiKeyAuth.Hash(raw);
        var key = await _repo.FindByHashAsync(hash, Context.RequestAborted).ConfigureAwait(false);
        if (key is null) return AuthenticateResult.Fail("Unknown API key.");
        if (!key.Enabled) return AuthenticateResult.Fail("API key disabled.");
        if (key.ExpiresAt is { } exp && exp <= DateTime.UtcNow)
            return AuthenticateResult.Fail("API key expired.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "apikey:" + key.Id),
            new(ClaimTypes.Name, key.Name),
            new("safeview.api_key_id", key.Id),
            new("safeview.api_key_prefix", key.Prefix)
        };
        foreach (var scope in key.Scopes.Distinct(StringComparer.Ordinal))
            claims.Add(new Claim(SafeViewClaims.Permission, scope));

        var identity = new ClaimsIdentity(claims, ApiKeyAuth.SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        // Best-effort touch (nie blokujemy odpowiedzi)
        _ = _repo.TouchUsedAsync(key.Id, DateTime.UtcNow, CancellationToken.None);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyAuth.SchemeName));
    }
}
