using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using SafeView.Infrastructure.Configuration;

namespace SafeView.Infrastructure.Persistence;

public sealed class MongoContext : IMongoContext
{
    private static int _conventionsRegistered;

    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }

    public MongoContext(IOptions<MongoOptions> options)
    {
        EnsureConventionsRegistered();
        var opts = options.Value;
        Client = new MongoClient(opts.ConnectionString);
        Database = Client.GetDatabase(opts.DatabaseName);
    }

    public IMongoCollection<T> Collection<T>(string name) => Database.GetCollection<T>(name);

    private static void EnsureConventionsRegistered()
    {
        if (Interlocked.Exchange(ref _conventionsRegistered, 1) == 1) return;

        var pack = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true),
            new EnumRepresentationConvention(BsonType.String)
        };
        ConventionRegistry.Register("SafeView.DefaultConventions", pack, _ => true);
    }
}
