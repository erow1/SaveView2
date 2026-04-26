namespace SafeView.Application.Configuration;

/// <summary>
/// Konfiguracja runtime ML (ONNX Runtime).
/// Edytowalna z /admin/settings → sekcja ML.
/// </summary>
public sealed class MLOptions
{
    public const string SectionName = "ML";

    /// <summary>
    /// Włączyć CUDA execution provider (wymagany <c>Microsoft.ML.OnnxRuntime.Gpu</c> + karta NVIDIA).
    /// Gdy true i GPU nie dostępne → fallback CPU z Warning w system_events.
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>Device ID dla CUDA (multi-GPU box). Default 0.</summary>
    public int GpuDeviceId { get; set; }

    /// <summary>
    /// Alternatywny provider gdy GPU niedostępne:
    /// <c>"directml"</c> (Windows AMD/Intel), <c>"coreml"</c> (macOS Apple Silicon), <c>""</c>=CPU.
    /// </summary>
    public string FallbackProvider { get; set; } = "";
}
