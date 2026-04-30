using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.Storage;
using SafeView.Application.Configuration;

namespace SafeView.Infrastructure.Storage;

/// <summary>
/// Prosta implementacja IFileStore zapisująca na lokalnym dysku w konfigurowalnych katalogach.
/// </summary>
public sealed class LocalDiskFileStore : IFileStore
{
    private readonly StorageOptions _options;
    private readonly ILogger<LocalDiskFileStore> _log;

    public LocalDiskFileStore(IOptions<StorageOptions> options, ILogger<LocalDiskFileStore> log)
    {
        _options = options.Value;
        _log = log;
        EnsureDirectoriesExist();
    }

    public async Task<string> SaveAsync(FileKind kind, string relativePath, Stream content, CancellationToken ct = default)
    {
        var absolute = ResolveAbsolutePath(kind, relativePath);
        var dir = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var fs = File.Create(absolute);
        await content.CopyToAsync(fs, ct);
        return relativePath;
    }

    public Task<Stream?> OpenReadAsync(FileKind kind, string relativePath, CancellationToken ct = default)
    {
        var absolute = ResolveAbsolutePath(kind, relativePath);
        if (!File.Exists(absolute))
            return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(File.OpenRead(absolute));
    }

    public Task<bool> DeleteAsync(FileKind kind, string relativePath, CancellationToken ct = default)
    {
        var absolute = ResolveAbsolutePath(kind, relativePath);
        if (!File.Exists(absolute))
            return Task.FromResult(false);
        File.Delete(absolute);
        return Task.FromResult(true);
    }

    public string ResolveAbsolutePath(FileKind kind, string relativePath)
    {
        var kindDir = kind switch
        {
            FileKind.Frame => _options.Frames,
            FileKind.Clip => _options.Clips,
            FileKind.Report => _options.Reports,
            FileKind.Model => _options.Models,
            FileKind.Upload => _options.Uploads,
            FileKind.License => _options.Licenses,
            FileKind.CameraMedia => _options.CameraMedia,
            FileKind.DetectionClassRef => _options.DetectionClassRefs,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var baseDir = Path.IsPathRooted(kindDir) ? kindDir : Path.Combine(RootAbsolute, kindDir);
        return Path.GetFullPath(Path.Combine(baseDir, relativePath));
    }

    public bool Exists(FileKind kind, string relativePath) => File.Exists(ResolveAbsolutePath(kind, relativePath));

    private string RootAbsolute => Path.IsPathRooted(_options.Root)
        ? _options.Root
        : Path.GetFullPath(_options.Root, AppContext.BaseDirectory);

    private void EnsureDirectoriesExist()
    {
        foreach (var kind in Enum.GetValues<FileKind>())
        {
            try
            {
                var dir = Path.GetDirectoryName(ResolveAbsolutePath(kind, "placeholder"));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (IOException ex)
            {
                // Don't crash the app if a storage path is on an unmounted / read-only volume.
                _log.LogWarning("Cannot create storage directory for {Kind}: {Error}", kind, ex.Message);
            }
        }
    }
}
