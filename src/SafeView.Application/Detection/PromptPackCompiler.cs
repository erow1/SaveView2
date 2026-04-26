using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.ML;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection;

/// <summary>
/// Produkcyjna impl <see cref="IPromptPackCompiler"/>. Koordynuje:
/// <list type="number">
///   <item>Załadowanie <see cref="DetectionClass"/> z repo (fail-fast na brakujących ID)</item>
///   <item>Walidację że wszystkie klasy mają TextPrompt (kind Text / TextAndVisual)</item>
///   <item>Encode przez aktywny <see cref="IClipTextEncoder"/> (InProcess lub External — zgodnie z YoloWorldOptions)</item>
///   <item>Konwersję flat-float → byte[] (little-endian) + zapis w Mongo</item>
/// </list>
/// </summary>
public sealed class PromptPackCompiler : IPromptPackCompiler
{
    private readonly IDetectionClassRepository _classes;
    private readonly ICompiledPromptPackRepository _packs;
    private readonly IClipTextEncoder _encoder;
    private readonly ILogger<PromptPackCompiler> _log;

    public PromptPackCompiler(
        IDetectionClassRepository classes,
        ICompiledPromptPackRepository packs,
        IClipTextEncoder encoder,
        ILogger<PromptPackCompiler> log)
    {
        _classes = classes;
        _packs = packs;
        _encoder = encoder;
        _log = log;
    }

    public async Task<CompiledPromptPack> CompileAsync(
        string sourceModelId,
        IReadOnlyList<string> classIds,
        string? name = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModelId);
        ArgumentNullException.ThrowIfNull(classIds);
        if (classIds.Count == 0)
            throw new ArgumentException("classIds pusta — podaj przynajmniej jedną klasę.", nameof(classIds));

        // Załaduj klasy zachowując kolejność podaną przez caller-a.
        var loaded = new List<DetectionClass>(classIds.Count);
        foreach (var id in classIds)
        {
            var c = await _classes.GetByIdAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"DetectionClass {id} nie istnieje.");
            if (c.Kind != DetectionClassKind.Text && c.Kind != DetectionClassKind.TextAndVisual)
                throw new InvalidOperationException(
                    $"Klasa '{c.Name}' ma kind={c.Kind} — do kompilacji packa text-embeddings wymagane są Text / TextAndVisual.");
            if (string.IsNullOrWhiteSpace(c.TextPrompt))
                throw new InvalidOperationException($"Klasa '{c.Name}' nie ma TextPrompt.");
            loaded.Add(c);
        }

        var prompts = loaded.Select(c => c.TextPrompt!).ToList();
        _log.LogInformation("Kompiluję prompt pack: model={Model}, encoder={Encoder}, klas={N}",
            sourceModelId, _encoder.Backend, prompts.Count);

        // Encode — pojedyncze wywołanie batch-owe (IClipTextEncoder liczy wszystkie prompty naraz).
        var flat = await _encoder.EncodeAsync(prompts, ct).ConfigureAwait(false);
        var dim = _encoder.EmbeddingDimension;
        if (flat.Length != prompts.Count * dim)
            throw new InvalidOperationException(
                $"Encoder zwrócił {flat.Length} floatów, oczekiwano {prompts.Count * dim} ({prompts.Count}×{dim}).");

        var bytes = new byte[flat.Length * sizeof(float)];
        Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);

        var pack = new CompiledPromptPack
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Pack ({prompts.Count} klas, {_encoder.Backend})" : name,
            SourceModelId = sourceModelId,
            EncoderBackend = _encoder.Backend,
            EmbeddingDimension = dim,
            ClassIds = loaded.Select(c => c.Id).ToList(),
            Prompts = prompts,
            EmbeddingsBlob = bytes,
            CompiledAt = DateTime.UtcNow,
            Stale = false,
            PromptSnapshots = loaded.ToDictionary(c => c.Id, c => c.TextPrompt!, StringComparer.Ordinal)
        };

        await _packs.InsertAsync(pack, ct).ConfigureAwait(false);
        _log.LogInformation("Pack '{Name}' skompilowany (id={Id}, {Bytes} bajtów blob)",
            pack.Name, pack.Id, bytes.Length);
        return pack;
    }

    public async Task<bool> RefreshStalenessAsync(string packId, CancellationToken ct = default)
    {
        var pack = await _packs.GetByIdAsync(packId, ct).ConfigureAwait(false);
        if (pack is null) return false;
        if (pack.Stale) return true;

        bool stale = false;
        foreach (var (classId, snapshotPrompt) in pack.PromptSnapshots)
        {
            var current = await _classes.GetByIdAsync(classId, ct).ConfigureAwait(false);
            if (current is null || !string.Equals(current.TextPrompt, snapshotPrompt, StringComparison.Ordinal))
            {
                stale = true;
                break;
            }
        }

        if (stale)
        {
            await _packs.MarkStaleAsync(packId, ct).ConfigureAwait(false);
            _log.LogInformation("Pack {Id} oznaczony jako stale", packId);
        }
        return stale;
    }
}
