using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Persistence;

/// <summary>
/// Repozytorium skompilowanych packów embeddings (Faza 5 — YOLO-World compilation).
/// Pack jest identyfikowany przez (<c>SourceModelId</c>, <c>EncoderBackend</c>, set of <c>ClassIds</c>).
/// </summary>
public interface ICompiledPromptPackRepository : IRepository<CompiledPromptPack>
{
    /// <summary>Wszystkie packi dla danego modelu źródłowego — UI listowanie.</summary>
    Task<IReadOnlyList<CompiledPromptPack>> ListBySourceModelAsync(string sourceModelId, CancellationToken ct = default);

    /// <summary>
    /// Wyszukuje pack dopasowujący set klas (dokładnie — zgodność order-insensitive).
    /// Używane przez pipeline do skipowania CLIP encode.
    /// </summary>
    Task<CompiledPromptPack?> FindMatchingAsync(
        string sourceModelId,
        string encoderBackend,
        IReadOnlyList<string> classIds,
        CancellationToken ct = default);

    /// <summary>Oznacza pack jako stale (do rekompilacji) — używane przy zmianie prompta klasy.</summary>
    Task MarkStaleAsync(string packId, CancellationToken ct = default);

    /// <summary>
    /// Wyszukuje pack zawierający identyczny zestaw promptów (order-insensitive) — używane
    /// przez <c>OnnxYoloWorldDetector</c> żeby pominąć CLIP encode gdy cache hit. Prompty
    /// są string porównywane dokładnie (ordinal, case-sensitive).
    /// </summary>
    Task<CompiledPromptPack?> FindByPromptsAsync(
        string sourceModelId,
        string encoderBackend,
        IReadOnlyList<string> prompts,
        CancellationToken ct = default);
}
