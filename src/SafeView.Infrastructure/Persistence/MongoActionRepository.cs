using MongoDB.Bson;
using MongoDB.Driver;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Security;
using SafeView.Domain.Detection;

namespace SafeView.Infrastructure.Persistence;

/// <summary>
/// Mongo repo dla <see cref="DetectionAction"/> z transparent encryption sensitive fields
/// (SMTP password, webhook secret, itp. — lista w <see cref="SecretsPolicy.SensitiveConfigKeys"/>).
///
/// Encryption jest **symetryczne**: przed Insert/Update → encrypt, po Get/List → decrypt.
/// Backward compat: wartości bez prefiksu <c>enc:v1:</c> traktowane jako legacy plaintext
/// (przy następnym Update zostaną zaszyfrowane).
/// </summary>
public sealed class MongoActionRepository : MongoRepositoryBase<DetectionAction>, IActionRepository
{
    public const string CollectionName = "actions";

    private readonly ISecretCipher _cipher;

    public MongoActionRepository(IMongoContext ctx, ISecretCipher cipher) : base(ctx, CollectionName)
    {
        _cipher = cipher;
        Col.Indexes.CreateOne(new CreateIndexModel<DetectionAction>(
            Builders<DetectionAction>.IndexKeys.Ascending(a => a.Enabled)));
    }

    // ── Read side — decrypt ──────────────────────────────────────────────────

    public override async Task<DetectionAction?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var action = await base.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (action is not null) DecryptConfig(action);
        return action;
    }

    public override async Task<IReadOnlyList<DetectionAction>> ListAsync(CancellationToken ct = default)
    {
        var list = await base.ListAsync(ct).ConfigureAwait(false);
        foreach (var a in list) DecryptConfig(a);
        return list;
    }

    public async Task<IReadOnlyList<DetectionAction>> ListByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var list = await Col.Find(a => ids.Contains(a.Id)).ToListAsync(ct).ConfigureAwait(false);
        foreach (var a in list) DecryptConfig(a);
        return list;
    }

    public async Task<IReadOnlyList<DetectionAction>> ListEnabledAsync(CancellationToken ct = default)
    {
        var list = await Col.Find(a => a.Enabled).ToListAsync(ct).ConfigureAwait(false);
        foreach (var a in list) DecryptConfig(a);
        return list;
    }

    // ── Write side — encrypt ─────────────────────────────────────────────────

    public override async Task InsertAsync(DetectionAction entity, CancellationToken ct = default)
    {
        // Encrypt kopię — nie mutujemy obiektu użytkownika
        var toStore = CloneWithEncryptedConfig(entity);
        await base.InsertAsync(toStore, ct).ConfigureAwait(false);
        // entity.Id został ustawiony przez base? Nie — base używa entity. Pozostawmy oryginalny bez szyfrowania (user dalej używa plaintext w pamięci).
    }

    public override async Task UpdateAsync(DetectionAction entity, CancellationToken ct = default)
    {
        var toStore = CloneWithEncryptedConfig(entity);
        toStore.UpdatedAt = DateTime.UtcNow;
        await Col.ReplaceOneAsync(x => x.Id == entity.Id, toStore, cancellationToken: ct).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private DetectionAction CloneWithEncryptedConfig(DetectionAction src)
    {
        var newConfig = new Dictionary<string, string>(src.Config.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in src.Config)
            newConfig[k] = SecretsPolicy.IsSensitive(k) ? _cipher.Encrypt(v) : v;

        return new DetectionAction
        {
            Id = src.Id,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt,
            Name = src.Name,
            Description = src.Description,
            Type = src.Type,
            Config = newConfig,
            Enabled = src.Enabled,
            RateLimitPerMinute = src.RateLimitPerMinute
        };
    }

    private void DecryptConfig(DetectionAction action)
    {
        // Decrypt in-place — kaller dostaje plaintext w Config
        var keys = action.Config.Keys.ToList();
        foreach (var k in keys)
        {
            if (!SecretsPolicy.IsSensitive(k)) continue;
            var v = action.Config[k];
            if (!_cipher.IsEncrypted(v)) continue; // legacy plaintext — leave as is
            try { action.Config[k] = _cipher.Decrypt(v); }
            catch (InvalidOperationException)
            {
                // Key rotation / tampered — zostaw placeholder, akcja i tak się wysypie przy handler execution
                action.Config[k] = "";
            }
        }
    }
}

public sealed class MongoActionExecutionRepository : IActionExecutionRepository
{
    public const string CollectionName = "action_executions";
    private readonly IMongoCollection<ActionExecution> _col;

    public MongoActionExecutionRepository(IMongoContext ctx)
    {
        _col = ctx.Collection<ActionExecution>(CollectionName);

        // TTL index 30 dni — MongoDB sam usuwa stare audit logi
        _ = Task.Run(async () =>
        {
            try
            {
                await _col.Indexes.CreateManyAsync([
                    new CreateIndexModel<ActionExecution>(
                        Builders<ActionExecution>.IndexKeys.Ascending(a => a.CreatedAt),
                        new CreateIndexOptions { Name = "ttl_created_at", ExpireAfter = TimeSpan.FromDays(30) }),
                    new CreateIndexModel<ActionExecution>(
                        Builders<ActionExecution>.IndexKeys.Descending(a => a.CreatedAt),
                        new CreateIndexOptions { Name = "created_at_desc" }),
                    new CreateIndexModel<ActionExecution>(
                        Builders<ActionExecution>.IndexKeys.Ascending(a => a.TriggerId)),
                    new CreateIndexModel<ActionExecution>(
                        Builders<ActionExecution>.IndexKeys.Ascending(a => a.ActionId)),
                    new CreateIndexModel<ActionExecution>(
                        Builders<ActionExecution>.IndexKeys.Ascending(a => a.CameraId)),
                ]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MongoActionExecutionRepository] Index creation failed: {ex.Message}");
            }
        });
    }

    public async Task LogAsync(ActionExecution execution, CancellationToken ct = default)
    {
        try
        {
            execution.UpdatedAt = DateTime.UtcNow;
            await _col.InsertOneAsync(execution, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MongoActionExecutionRepository] Insert failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ActionExecution>> QueryAsync(
        ActionExecutionFilter filter, int skip, int take, CancellationToken ct = default)
    {
        var f = BuildFilter(filter);
        return await _col.Find(f)
            .SortByDescending(e => e.CreatedAt)
            .Skip(skip).Limit(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<long> CountAsync(ActionExecutionFilter filter, CancellationToken ct = default)
        => _col.CountDocumentsAsync(BuildFilter(filter), cancellationToken: ct);

    private static FilterDefinition<ActionExecution> BuildFilter(ActionExecutionFilter filter)
    {
        var fb = Builders<ActionExecution>.Filter;
        var f = fb.Empty;
        if (!string.IsNullOrWhiteSpace(filter.ActionId)) f &= fb.Eq(e => e.ActionId, filter.ActionId);
        if (!string.IsNullOrWhiteSpace(filter.TriggerId)) f &= fb.Eq(e => e.TriggerId, filter.TriggerId);
        if (!string.IsNullOrWhiteSpace(filter.CameraId)) f &= fb.Eq(e => e.CameraId, filter.CameraId);
        if (filter.Status is { } s) f &= fb.Eq(e => e.Status, s);
        if (filter.Since is { } since) f &= fb.Gte(e => e.CreatedAt, since);
        return f;
    }
}
