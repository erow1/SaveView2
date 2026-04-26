namespace SafeView.Application.Abstractions.LLM;

/// <summary>
/// Factory <see cref="IEmbeddingsClient"/> — analogicznie do <see cref="IChatClientFactory"/>.
/// Resolvuje klienta dla providerId z DB, cache per-provider (HttpClient + bearer).
/// </summary>
public interface IEmbeddingsClientFactory
{
    /// <summary>Klient dla providera. Null / empty → default (z <c>LlmOptions</c>).</summary>
    Task<IEmbeddingsClient> GetForAsync(string? providerId, CancellationToken ct = default);

    /// <summary>Unieważnia cache dla providera po zmianie config.</summary>
    void Invalidate(string providerId);
}
