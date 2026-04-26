namespace SafeView.Application.Abstractions.LLM;

/// <summary>
/// Factory dla <see cref="IChatClient"/> — resolvuje klienta dla danego providerId z DB.
/// Gdy providerId == null → zwraca klienta domyślnego (z fallback do appsettings Llm).
/// Implementacja cache'uje klientów per providerId żeby nie tworzyć HttpClient przy każdym calł.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>Klient dla konkretnego providera. Null / empty → default.</summary>
    Task<IChatClient> GetForAsync(string? providerId, CancellationToken ct = default);

    /// <summary>Unieważnia cache dla providera (gdy user zmienił URL/klucz). Wywołuj po UpdateAsync.</summary>
    void Invalidate(string providerId);
}
