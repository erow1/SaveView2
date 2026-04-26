using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SafeView.Application.Abstractions.Media;

namespace SafeView.Cameras;

public static class DependencyInjection
{
    public static IServiceCollection AddSafeViewCameras(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MediaMtxOptions>()
            .Bind(configuration.GetSection(MediaMtxOptions.SectionName));
        services.AddOptions<FfmpegOptions>()
            .Bind(configuration.GetSection(FfmpegOptions.SectionName));
        services.AddOptions<CameraSamplerOptions>()
            .Bind(configuration.GetSection(CameraSamplerOptions.SectionName));

        services.AddSingleton<ISnapshotService, FfmpegSnapshotService>();
        services.AddSingleton<MediaMtxManager>();
        services.AddHostedService<CameraFrameSampler>();
        services.AddHostedService<MediaMtxRunner>();
        return services;
    }
}
