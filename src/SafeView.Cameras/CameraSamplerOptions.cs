namespace SafeView.Cameras;

public sealed class CameraSamplerOptions
{
    public const string SectionName = "Cameras:Sampler";

    /// <summary>Gdy false, sampler w ogóle się nie uruchamia (UI/manualne snapshoty działają).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maksymalna liczba równoległych snapshotów (chroni CPU i ffmpeg).</summary>
    public int MaxConcurrentCaptures { get; set; } = 4;

    /// <summary>
    /// Minimalne opóźnienie między ticki samplera (milisekundy).
    /// Sampler budzi się zgodnie z <see cref="Camera.SnapshotIntervalSeconds"/> każdej kamery,
    /// ale nie częściej niż ta wartość — chroni CPU gdy user ustawi bardzo krótki interval.
    /// </summary>
    public int MinTickMilliseconds { get; set; } = 250;

    /// <summary>Opóźnienie startu samplera po starcie aplikacji (pozwala UI wstać).</summary>
    public int StartupDelaySeconds { get; set; } = 5;
}
