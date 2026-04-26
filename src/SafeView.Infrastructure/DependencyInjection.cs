using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SafeView.Application.Abstractions.Diagnostics;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Storage;
using SafeView.Application.Abstractions.Time;
using SafeView.Application.Configuration;
using SafeView.Infrastructure.Configuration;
using SafeView.Infrastructure.Persistence;
using SafeView.Infrastructure.Storage;
using SafeView.Infrastructure.Time;

namespace SafeView.Infrastructure;

/// <summary>
/// Rejestracja infrastruktury (MongoDB + FileStore) w kontenerze DI.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MongoOptions>()
            .Bind(configuration.GetSection(MongoOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<DiagnosticsOptions>()
            .Bind(configuration.GetSection(DiagnosticsOptions.SectionName));

        services.AddSingleton<IMongoContext, MongoContext>();
        services.AddSingleton<IFileStore, LocalDiskFileStore>();

        // Repozytoria — singleton OK, IMongoCollection jest thread-safe
        services.AddSingleton<IUserRepository, MongoUserRepository>();
        services.AddSingleton<IRoleRepository, MongoRoleRepository>();
        services.AddSingleton<ICameraRepository, MongoCameraRepository>();
        services.AddSingleton<IMLModelRepository, MongoMLModelRepository>();
        services.AddSingleton<IIncidentRepository, MongoIncidentRepository>();
        services.AddSingleton<IZoneRepository, MongoZoneRepository>();
        services.AddSingleton<IApiKeyRepository, MongoApiKeyRepository>();
        services.AddSingleton<MongoAuditLogger>();
        services.AddSingleton<IAuditLogger>(sp => sp.GetRequiredService<MongoAuditLogger>());
        services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<MongoAuditLogger>());

        // System events (diagnostyka techniczna — MediaMTX/ffmpeg/Mongo/storage errors)
        services.AddSingleton<MongoSystemEventLogger>();
        services.AddSingleton<ISystemEventLogger>(sp => sp.GetRequiredService<MongoSystemEventLogger>());
        services.AddSingleton<ISystemEventQuery>(sp => sp.GetRequiredService<MongoSystemEventLogger>());

        // Performance metrics — in-memory rolling window (Faza 5 dashboard)
        services.AddSingleton<IPerformanceMetrics, SafeView.Application.Diagnostics.PerformanceMetrics>();

        // Detection pipeline Faza 1 — ROI + Trigger + Action + audit
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRoiRepository, MongoRoiRepository>();
        services.AddSingleton<ITriggerRepository, MongoTriggerRepository>();
        services.AddSingleton<IActionRepository, MongoActionRepository>();
        services.AddSingleton<IActionExecutionRepository, MongoActionExecutionRepository>();
        services.AddSingleton<IPromptTemplateVersionRepository, MongoPromptTemplateVersionRepository>();
        services.AddSingleton<IPromptTemplateRepository, MongoPromptTemplateRepository>();
        services.AddSingleton<IDetectionClassRepository, MongoDetectionClassRepository>();
        services.AddSingleton<ICompiledPromptPackRepository, MongoCompiledPromptPackRepository>();
        services.AddSingleton<ILlmProviderRepository, MongoLlmProviderRepository>();

        // Hosted service — jednorazowe sprzątanie legacy Zone dokumentów ze starego schema
        services.AddHostedService<LegacyZoneCleanupService>();

        // Hosted service — auto-rejestracja bundled modeli ONNX z runtime/models/
        services.AddHostedService<ML.ModelSeeder>();

        // Hosted service — seeduje wbudowane szablony promptów VLLM dla BHP (12 scenariuszy)
        services.AddHostedService<Vllm.PromptTemplateSeeder>();

        // Hosted service — seeduje wbudowane klasy detekcji BHP (PPE, pożar, ruch, bezpieczeństwo)
        services.AddHostedService<Detection.DetectionClassSeeder>();

        return services;
    }
}
