using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Common;

namespace SafeView.Infrastructure.Persistence;

public abstract class MongoRepositoryBase<T> : IRepository<T> where T : Entity
{
    protected IMongoCollection<T> Col { get; }

    protected MongoRepositoryBase(IMongoContext ctx, string collectionName)
    {
        Col = ctx.Collection<T>(collectionName);
    }

    public virtual async Task<T?> GetByIdAsync(string id, CancellationToken ct = default)
        => await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public virtual async Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default)
        => await Col.Find(FilterDefinition<T>.Empty).ToListAsync(ct).ConfigureAwait(false);

    public virtual async Task InsertAsync(T entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        await Col.InsertOneAsync(entity, cancellationToken: ct).ConfigureAwait(false);
    }

    public virtual async Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        await Col.ReplaceOneAsync(x => x.Id == entity.Id, entity, cancellationToken: ct).ConfigureAwait(false);
    }

    public virtual async Task DeleteAsync(string id, CancellationToken ct = default)
        => await Col.DeleteOneAsync(x => x.Id == id, ct).ConfigureAwait(false);
}
