using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Kompiluje <see cref="CompiledPromptPack"/> z listy <see cref="DetectionClass"/> —
/// liczy text embeddings przez aktywny <see cref="SafeView.Application.Abstractions.ML.IClipTextEncoder"/>
/// i zapisuje jako blob w Mongo.
///
/// <para>Efekt użycia: kolejne inferencje z tym samym zestawem klas pomijają CLIP encode
/// (oszczędność ~10-30ms per klatka). Pack jest persistent (przetrwuje restart) i weryfikowany
/// przez snapshot promptów przy invalidation.</para>
/// </summary>
public interface IPromptPackCompiler
{
    /// <summary>
    /// Kompiluje nowy pack dla podanego modelu YOLO-World i listy klas.
    /// Wszystkie klasy muszą mieć <see cref="DetectionClassKind.Text"/> albo
    /// <see cref="DetectionClassKind.TextAndVisual"/> (muszą mieć TextPrompt). Rzuca gdy brak promptu.
    /// </summary>
    /// <param name="sourceModelId">ID modelu YOLO-World.</param>
    /// <param name="classIds">Klasy w kolejności (Index w output modelu będzie odpowiadał indeksowi tu).</param>
    /// <param name="name">Nazwa user-friendly (null = generowana z liczby klas).</param>
    Task<CompiledPromptPack> CompileAsync(
        string sourceModelId,
        IReadOnlyList<string> classIds,
        string? name = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sprawdza czy pack jest aktualny — porównuje <see cref="CompiledPromptPack.PromptSnapshots"/>
    /// z aktualnymi <see cref="DetectionClass.TextPrompt"/>. Gdy cokolwiek się zmieniło,
    /// oznacza pack jako stale (<see cref="CompiledPromptPack.Stale"/>=true).
    /// </summary>
    Task<bool> RefreshStalenessAsync(string packId, CancellationToken ct = default);
}
