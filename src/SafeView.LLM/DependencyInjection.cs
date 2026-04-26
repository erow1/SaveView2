using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.LLM;
using SafeView.Application.Abstractions.Vllm;

namespace SafeView.LLM;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewLLM(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName));

        services.AddHttpClient<IChatClient, OpenAiCompatibleChatClient>();
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
