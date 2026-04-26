using System.Text.Json;
using FluentAssertions;
using SafeView.ML.YoloWorld;

namespace SafeView.ML.Tests;

/// <summary>
/// Testy <see cref="ClipTokenizer"/> z mini-vocabem (bez pobierania prawdziwego CLIP ~2MB).
/// Sprawdzamy shape wyjścia, sentinel tokens (SOS/EOS), padding, normalizację tekstu
/// oraz że BPE merge faktycznie się wykonuje.
/// </summary>
public class ClipTokenizerTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vocabPath;
    private readonly string _mergesPath;

    public ClipTokenizerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "safeview-clip-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _vocabPath = Path.Combine(_tmpDir, "vocab.json");
        _mergesPath = Path.Combine(_tmpDir, "merges.txt");

        // Minimalny vocab: litery + parę merged tokens + sentinels
        // Real CLIP ma 49408 tokenów; my mamy ~30 wystarczających do testów.
        var vocab = new Dictionary<string, int>
        {
            ["h"] = 1, ["e"] = 2, ["l"] = 3, ["o"] = 4, ["i"] = 5, ["n"] = 6, ["t"] = 7,
            ["h</w>"] = 10, ["e</w>"] = 11, ["l</w>"] = 12, ["o</w>"] = 13,
            ["hi</w>"] = 20, ["hello</w>"] = 21, ["test</w>"] = 22,
            ["he"] = 30, ["hel"] = 31, ["hell"] = 32, ["hello"] = 33,
            ["te"] = 40, ["tes"] = 41, ["test"] = 42,
            // Sentinels używane przez ClipTokenizer (SOS=49406, EOS=49407). W wyjściu
            // tokenizera te ids pojawiają się jawnie, nie muszą być w vocab.
        };
        File.WriteAllText(_vocabPath, JsonSerializer.Serialize(vocab));

        // Merges: priority kolejnością (niższy rank = wcześniej)
        File.WriteAllText(_mergesPath, string.Join('\n',
            "#version: test",
            "h e",        // rank 0 — "he"
            "he l",       // rank 1 — "hel"
            "hel l",      // rank 2 — "hell"
            "hell o</w>", // rank 3 — "hello</w>" (ten z </w>)
            "t e",        // rank 4 — "te"
            "te s",       // rank 5 — "tes"
            "tes t</w>",  // rank 6 — "test</w>"
            "h i</w>"     // rank 7 — "hi</w>"
        ));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Tokenize_ReturnsContextLengthArray_WithSosAndEosSentinels()
    {
        var t = new ClipTokenizer(_vocabPath, _mergesPath, contextLength: 16);

        var ids = t.Tokenize("hello");

        ids.Length.Should().Be(16, "context length = pad/truncate do 16");
        ids[0].Should().Be(49406, "SOS token musi być pierwszy");
        // EOS powinien być na końcu contentu, reszta pad=0
        ids.Should().Contain(49407, "EOS token musi wystąpić");
        ids[^1].Should().Be(0, "ostatni token = pad");
    }

    [Fact]
    public void Tokenize_AppliesBpeMerges_AndResolvesToKnownVocab()
    {
        var t = new ClipTokenizer(_vocabPath, _mergesPath, contextLength: 16);

        var ids = t.Tokenize("hello");

        // Po merge "h e → he → hel → hell → hello</w>" dostajemy jeden token id=21 (hello</w>)
        ids.Should().Contain(21, "'hello' po BPE merge powinno zostać jednym tokenem hello</w>");
    }

    [Fact]
    public void Tokenize_LowercasesInput()
    {
        var t = new ClipTokenizer(_vocabPath, _mergesPath, contextLength: 16);

        var lower = t.Tokenize("hello");
        var upper = t.Tokenize("HELLO");

        upper.Should().BeEquivalentTo(lower, "tokenizer CLIP robi lower-case przed tokenization");
    }

    [Fact]
    public void Tokenize_EmptyString_ProducesSosThenEos()
    {
        var t = new ClipTokenizer(_vocabPath, _mergesPath, contextLength: 16);

        var ids = t.Tokenize("");

        ids[0].Should().Be(49406);
        ids[1].Should().Be(49407, "brak tokenów treści → EOS zaraz po SOS");
        for (int i = 2; i < 16; i++) ids[i].Should().Be(0);
    }

    [Fact]
    public void Tokenize_Truncates_WhenContentLongerThanContextLength()
    {
        // Krótki context → forced truncation + ostatni to EOS
        var t = new ClipTokenizer(_vocabPath, _mergesPath, contextLength: 5);

        var ids = t.Tokenize("hello hello hello hello");

        ids.Length.Should().Be(5);
        ids[0].Should().Be(49406);
        ids[4].Should().Be(49407, "ostatni slot zawsze EOS przy truncation");
    }

    [Fact]
    public void Tokenize_ThrowsOnMissingFiles_LazyAtFirstUse()
    {
        // Lazy-load: ctor przechodzi nawet gdy pliki brak (żeby DI mogło zarejestrować instancję
        // zanim `download-models.sh` się wykona). Pierwsze Tokenize() rzuca FileNotFoundException
        // z czytelnym komunikatem wskazującym skrypt do pobrania modelu.
        var t1 = new ClipTokenizer("/nonexistent/vocab.json", _mergesPath);
        var t2 = new ClipTokenizer(_vocabPath, "/nonexistent/merges.txt");

        var act1 = () => t1.Tokenize("hello");
        var act2 = () => t2.Tokenize("hello");

        act1.Should().Throw<FileNotFoundException>().WithMessage("*vocab*");
        act2.Should().Throw<FileNotFoundException>().WithMessage("*merges*");
    }
}
