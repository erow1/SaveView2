using System.Collections.Concurrent;

namespace SafeView.Web.Services;

/// <summary>
/// In-memory dedup dla <c>frame_id</c> przesłanych przez ingest endpoint.
/// LRU per kamera (max 1000 ID-ów × N kamer). Reset przy restarcie aplikacji.
///
/// Best-effort dedup — chroni przed retry storm-em sendera. Nie gwarantuje exactly-once
/// w multi-instance deployment (in-process). Production-grade = Mongo TTL collection (follow-up).
/// </summary>
public sealed class IngestIdempotencyStore
{
    private const int MaxPerCamera = 1000;
    private readonly ConcurrentDictionary<string, LinkedHashSet<string>> _seen = new(StringComparer.Ordinal);

    /// <summary>Próbuje zarejestrować frame_id dla kamery. Zwraca false gdy już widziany.</summary>
    public bool TryAdd(string cameraId, string frameId)
    {
        if (string.IsNullOrWhiteSpace(frameId)) return true; // bez frame_id zawsze accept

        var bucket = _seen.GetOrAdd(cameraId, _ => new LinkedHashSet<string>(MaxPerCamera));
        return bucket.Add(frameId);
    }

    /// <summary>Bounded LinkedHashSet (FIFO eviction). Niewydajny przy lookup ale
    /// dla 1000 ID-ów × N kamer wystarcza. Synchronized przez lock.</summary>
    private sealed class LinkedHashSet<T> where T : notnull
    {
        private readonly LinkedList<T> _order = new();
        private readonly Dictionary<T, LinkedListNode<T>> _index;
        private readonly int _capacity;
        private readonly object _lock = new();

        public LinkedHashSet(int capacity)
        {
            _capacity = capacity;
            _index = new Dictionary<T, LinkedListNode<T>>(capacity);
        }

        public bool Add(T item)
        {
            lock (_lock)
            {
                if (_index.ContainsKey(item)) return false;
                if (_index.Count >= _capacity)
                {
                    var first = _order.First!;
                    _order.RemoveFirst();
                    _index.Remove(first.Value);
                }
                var node = _order.AddLast(item);
                _index[item] = node;
                return true;
            }
        }
    }
}
