namespace SafeView.Application.Abstractions.Persistence;

/// <summary>
/// DB-agnostyczna abstrakcja repozytorium. Implementacja MongoDB trafia do Infrastructure.
/// </summary>
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default);
    Task InsertAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
