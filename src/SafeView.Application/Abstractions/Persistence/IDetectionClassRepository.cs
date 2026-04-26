using SafeView.Domain.Detection;

namespace SafeView.Application.Abstractions.Persistence;

/// <summary>
/// Repozytorium <see cref="DetectionClass"/> — biblioteka klas detekcji (analog do
/// <see cref="IPromptTemplateRepository"/>). Built-in seedowane przez
/// <c>DetectionClassSeeder</c>, user-custom edytowalne w <c>/admin/detection-classes</c>.
/// </summary>
public interface IDetectionClassRepository : IRepository<DetectionClass>
{
    /// <summary>Klasy pogrupowane per kategoria, posortowane po nazwie.</summary>
    Task<IReadOnlyList<DetectionClass>> ListByCategoryAsync(string category, CancellationToken ct = default);

    /// <summary>Built-in klasa po stabilnym kluczu — używane przez seeder do re-sync.</summary>
    Task<DetectionClass?> GetByBuiltInKeyAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Znajduje klasę closed-set zbindowaną do konkretnej pary (modelId, label). Używane przy
    /// migracji starych <c>TriggerCondition.Labels</c> → <c>TriggerCondition.DetectionClassId</c>.
    /// </summary>
    Task<DetectionClass?> FindByClosedSetBindingAsync(string modelId, string label, CancellationToken ct = default);

    /// <summary>Batch lookup — używane przez DetectionPipeline do rezolucji klas w triggerach.</summary>
    Task<IReadOnlyList<DetectionClass>> ListByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default);
}
