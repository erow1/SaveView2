namespace SafeView.Application.Abstractions.LLM;

/// <summary>
/// Factory dla <see cref="IChatClient"/> — single source of truth: <c>LlmProvider</c> z Mongo
/// (zarządzane na <c>/admin/llm-providers</c>).
///
/// Gdy <c>providerId</c> jest null/empty → zwraca klienta dla providera oznaczonego
/// <c>IsDefault=true</c>. Gdy żaden provider nie jest skonfigurowany → rzuca
/// <see cref="InvalidOperationException"/> z linkiem do strony konfiguracji.
///
/// Implementacja cache'uje klientów per providerId żeby nie tworzyć <see cref="HttpClient"/>
/// przy każdym callu. <see cref="Invalidate"/> czyści cache po edycji providera w UI.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Klient dla konkretnego providera. Null / empty → default (provider z <c>IsDefault=true</c>).
    /// Throws <see cref="InvalidOperationException"/> gdy brak skonfigurowanego providera.
    /// </summary>
    Task<IChatClient> GetForAsync(string? providerId, CancellationToken ct = default);

    /// <summary>Unieważnia cache dla providera (gdy user zmienił URL/klucz). Wywołuj po UpdateAsync.</summary>
    void Invalidate(string providerId);
}
