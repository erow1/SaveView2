namespace SafeView.Application.Abstractions.LLM;

/// <summary>
/// Factory <see cref="IEmbeddingsClient"/> — analogicznie do <see cref="IChatClientFactory"/>.
/// Single source of truth: <c>LlmProvider</c> z Mongo (<c>/admin/llm-providers</c>).
/// Resolvuje klienta dla providerId z DB, cache per-provider (HttpClient + bearer).
/// </summary>
public interface IEmbeddingsClientFactory
{
    /// <summary>
    /// Klient dla providera. Null / empty → default (provider z <c>IsDefault=true</c>).
    /// Throws <see cref="InvalidOperationException"/> gdy brak skonfigurowanego providera.
    /// </summary>
    Task<IEmbeddingsClient> GetForAsync(string? providerId, CancellationToken ct = default);

    /// <summary>Unieważnia cache dla providera po zmianie config.</summary>
    void Invalidate(string providerId);
}
