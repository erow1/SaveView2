using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.LLM;
using SafeView.Application.Abstractions.Vllm;

namespace SafeView.LLM;

public static class DependencyInjection
{
    /// <summary>
    /// Rejestruje LLM/VLLM stack. Single source of truth: <c>LlmProvider</c> w Mongo
    /// (zarządzane na <c>/admin/llm-providers</c>). Brak bindowania z appsettings — jeśli żaden
    /// provider nie jest skonfigurowany, factories rzucają <see cref="InvalidOperationException"/>
    /// przy pierwszym użyciu, a UI pokazuje empty-state z linkiem do strony konfiguracji.
    /// </summary>
    public static IServiceCollection AddSafeViewLLM(this IServiceCollection services)
    {
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<IEmbeddingsClientFactory, EmbeddingsClientFactory>();
        services.AddSingleton<IIncidentAnalyzer, IncidentAnalyzer>();

        // VLLM-based trigger validation (Faza 3)
        services.AddSingleton<IVllmChecker, VllmChecker>();

        // Generator szablonów VLLM z opisu user-a (Faza C)
        services.AddSingleton<IPromptGenerator, LlmPromptGenerator>();

        return services;
    }
}
