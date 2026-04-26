using MongoDB.Driver;

namespace SafeView.Infrastructure.Persistence;

/// <summary>
/// Dostęp do bazy MongoDB — obiekty klienta i bazy, oraz typowana pobranka kolekcji.
/// </summary>
public interface IMongoContext
{
    IMongoClient Client { get; }
    IMongoDatabase Database { get; }
    IMongoCollection<T> Collection<T>(string name);
}
