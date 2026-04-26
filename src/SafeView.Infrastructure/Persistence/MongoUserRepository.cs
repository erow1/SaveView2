using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Users;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoUserRepository : MongoRepositoryBase<User>, IUserRepository
{
    public const string CollectionName = "users";

    public MongoUserRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        var unique = new CreateIndexOptions { Unique = true };
        Col.Indexes.CreateMany([
            new CreateIndexModel<User>(Builders<User>.IndexKeys.Ascending(u => u.Username), unique),
            new CreateIndexModel<User>(Builders<User>.IndexKeys.Ascending(u => u.Email), unique)
        ]);
    }

    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
        => Col.Find(u => u.Username == username).FirstOrDefaultAsync(ct)!;

    public Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
        => Col.Find(u => u.Email == email).FirstOrDefaultAsync(ct)!;

    public Task<long> CountAsync(CancellationToken ct = default)
        => Col.CountDocumentsAsync(FilterDefinition<User>.Empty, cancellationToken: ct);
}

public sealed class MongoRoleRepository : MongoRepositoryBase<Role>, IRoleRepository
{
    public const string CollectionName = "roles";

    public MongoRoleRepository(IMongoContext ctx) : base(ctx, CollectionName)
    {
        Col.Indexes.CreateOne(new CreateIndexModel<Role>(
            Builders<Role>.IndexKeys.Ascending(r => r.Name),
            new CreateIndexOptions { Unique = true }));
    }

    public Task<Role?> FindByNameAsync(string name, CancellationToken ct = default)
        => Col.Find(r => r.Name == name).FirstOrDefaultAsync(ct)!;
}
