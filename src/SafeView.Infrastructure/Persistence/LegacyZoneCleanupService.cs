using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SafeView.Infrastructure.Persistence;

/// <summary>
/// Jednorazowe sprzątanie przy starcie aplikacji — usuwa dokumenty ze starego schema Zone
/// (te które mają stare pole <c>rules</c> albo nie mają <c>roiId</c>). Nowy pipeline wymaga
/// <c>RoiId</c> i nie używa <c>Rules</c>.
///
/// Decyzja z planu: czysty restart, bez migracji. User re-definiuje strefy w nowym UI.
/// Safe na idempotent uruchamianie — gdy wszystko jest już w nowym schema, no-op.
/// </summary>
public sealed class LegacyZoneCleanupService : IHostedService
{
    private readonly IMongoContext _ctx;
    private readonly ILogger<LegacyZoneCleanupService> _log;

    public LegacyZoneCleanupService(IMongoContext ctx, ILogger<LegacyZoneCleanupService> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Używamy BsonDocument, nie typowanej kolekcji — bo deserializacja starego schema
            // na nowy Zone może rzucać (zmienione pola). Filtr na poziomie BSON:
            // brakuje "roiId" ALBO istnieje stare "rules".
            var coll = _ctx.Database.GetCollection<BsonDocument>("zones");

            var filter = Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Exists("roiId", false),
                Builders<BsonDocument>.Filter.Eq("roiId", BsonString.Empty),
                Builders<BsonDocument>.Filter.Exists("rules", true)
            );

            var count = await coll.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                var res = await coll.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
                _log.LogWarning(
                    "Usunięto {Count} legacy dokumentów z kolekcji 'zones' (stary schema bez roiId lub z polem 'rules'). " +
                    "User musi zdefiniować strefy od nowa w nowym UI.",
                    res.DeletedCount);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LegacyZoneCleanupService failed — strefy z starego schema mogą zostać w bazie.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
