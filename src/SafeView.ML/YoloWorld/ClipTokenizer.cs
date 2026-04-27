using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeView.ML.YoloWorld;

/// <summary>
/// CLIP BPE (Byte-Pair Encoding) tokenizer — minimalna self-contained implementacja dla
/// openai/clip-vit-base-patch32 vocab (tego samego używa YOLO-World v2). Implementacja
/// nie zależy od zewnętrznych paczek, żeby nie narażać się na breaking changes
/// w tokenizers libraries.
///
/// <para>Algorytm:</para>
/// <list type="number">
///   <item>Normalize: lowercase + squeeze whitespace</item>
///   <item>Tokenize po regex (słowa + pojedyncze znaki interpunkcyjne)</item>
///   <item>Dla każdego słowa: split na znaki + <c>&lt;/w&gt;</c> sufiks na ostatnim</item>
///   <item>BPE merge: greedy, najniższy rank merge aż brak kandydatów</item>
///   <item>Lookup w vocab → int ids</item>
///   <item>Wrap [SOS, ..., EOS] + pad/truncate do context length (77)</item>
/// </list>
///
/// <para>Wymaga dwóch plików z dystrybucji CLIP (w <c>tokenizer/</c> obok modelu):</para>
/// <list type="bullet">
///   <item><c>vocab.json</c> — mapa token-string → id (49408 tokenów)</item>
///   <item><c>merges.txt</c> — pary do mergowania (pierwsza linia to header "#version: ...")</item>
/// </list>
/// </summary>
public sealed class ClipTokenizer
{
    private const int SosToken = 49406;
    private const int EosToken = 49407;
    // CLIP konwencja: pad token = EOS (NIE 0). Token id 0 to "!" w vocab CLIP-a — padding nim
    // kontaminuje embedding ("!" idzie przez wszystkie attention layers transformera). Hugging Face
    // CLIPTokenizer też pad-uje EOS-em (sprawdzone porównawczo). Bug naprawiony 2026-04-27.
    private const int PadToken = EosToken;

    private static readonly Regex WordRegex = new(
        @"[\p{L}\p{M}]+|\p{N}+|[^\s\p{L}\p{M}\p{N}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly string _vocabPath;
    private readonly string _mergesPath;
    private readonly int _contextLength;
    private readonly Lazy<(Dictionary<string, int> Vocab, Dictionary<(string, string), int> Merges)> _data;

    /// <summary>
    /// Konstruktor zapisuje tylko ścieżki — vocab + merges są ładowane <b>lazy</b> przy pierwszym
    /// <see cref="Tokenize"/>. Pozwala DI zarejestrować instancję nawet gdy pliki jeszcze nie
    /// zostały pobrane (<c>scripts/download-models.sh yolo-world-v2-s</c>); błąd pojawia się
    /// dopiero gdy user faktycznie próbuje zakodować prompt.
    /// </summary>
    public ClipTokenizer(string vocabJsonPath, string mergesPath, int contextLength = 77)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mergesPath);

        _vocabPath = vocabJsonPath;
        _mergesPath = mergesPath;
        _contextLength = contextLength > 0 ? contextLength : 77;
        _data = new Lazy<(Dictionary<string, int>, Dictionary<(string, string), int>)>(() =>
        {
            if (!File.Exists(_vocabPath))
                throw new FileNotFoundException(
                    $"CLIP vocab nie znaleziono: {_vocabPath}. Uruchom scripts/download-models.sh yolo-world-v2-s.",
                    _vocabPath);
            if (!File.Exists(_mergesPath))
                throw new FileNotFoundException(
                    $"CLIP merges nie znaleziono: {_mergesPath}. Uruchom scripts/download-models.sh yolo-world-v2-s.",
                    _mergesPath);
            return (LoadVocab(_vocabPath), LoadMerges(_mergesPath));
        });
    }

    /// <summary>
    /// Tokenizuje tekst do listy int ids o stałej długości <see cref="_contextLength"/>.
    /// Pad token 0 dopełnia, EOS marker na końcu treści. Format zgodny z CLIP ONNX input.
    /// </summary>
    public long[] Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Force lazy load (rzuca FileNotFoundException z czytelnym komunikatem gdy pliki brak).
        var (vocab, merges) = _data.Value;

        var ids = new List<long>(_contextLength) { SosToken };

        var normalized = WhitespaceRegex.Replace(text.ToLowerInvariant(), " ").Trim();
        if (normalized.Length > 0)
        {
            foreach (Match match in WordRegex.Matches(normalized))
            {
                foreach (var id in EncodeWord(match.Value, vocab, merges))
                {
                    if (ids.Count >= _contextLength - 1) break; // zostaw miejsce na EOS
                    ids.Add(id);
                }
                if (ids.Count >= _contextLength - 1) break;
            }
        }

        ids.Add(EosToken);
        while (ids.Count < _contextLength) ids.Add(PadToken);

        return ids.ToArray();
    }

    private static IEnumerable<int> EncodeWord(string word,
        Dictionary<string, int> vocab, Dictionary<(string, string), int> mergeRanks)
    {
        if (string.IsNullOrEmpty(word)) yield break;

        // Split na znaki; ostatni dostaje </w> marker
        var tokens = new List<string>(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            tokens.Add(i == word.Length - 1 ? word[i] + "</w>" : word[i].ToString());
        }

        // Greedy BPE merges — najniższy rank wygrywa, iteruj aż brak merge'a
        while (tokens.Count > 1)
        {
            int bestRank = int.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                if (mergeRanks.TryGetValue((tokens[i], tokens[i + 1]), out var r) && r < bestRank)
                {
                    bestRank = r;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break;

            tokens[bestIdx] = tokens[bestIdx] + tokens[bestIdx + 1];
            tokens.RemoveAt(bestIdx + 1);
        }

        foreach (var t in tokens)
        {
            if (vocab.TryGetValue(t, out var id))
                yield return id;
            // silently skip tokens not in vocab (typowo nie powinno się zdarzyć dla UTF-8 ASCII)
        }
    }

    private static Dictionary<string, int> LoadVocab(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, int>(doc.RootElement.GetPropertyCount(), StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.GetInt32();
        }
        return result;
    }

    private static Dictionary<(string, string), int> LoadMerges(string path)
    {
        var ranks = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue; // header (#version: ...)
            var idx = line.IndexOf(' ');
            if (idx <= 0 || idx >= line.Length - 1) continue;
            var a = line[..idx];
            var b = line[(idx + 1)..];
            ranks[(a, b)] = rank++;
        }
        return ranks;
    }
}
