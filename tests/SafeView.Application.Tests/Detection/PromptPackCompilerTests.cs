using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SafeView.Application.Abstractions.ML;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Detection;
using SafeView.Domain.Detection;

namespace SafeView.Application.Tests.Detection;

public class PromptPackCompilerTests
{
    private readonly IDetectionClassRepository _classes = Substitute.For<IDetectionClassRepository>();
    private readonly ICompiledPromptPackRepository _packs = Substitute.For<ICompiledPromptPackRepository>();
    private readonly IClipTextEncoder _encoder = Substitute.For<IClipTextEncoder>();

    private PromptPackCompiler Build() => new(_classes, _packs, _encoder,
        NullLogger<PromptPackCompiler>.Instance);

    private static DetectionClass TextClass(string id, string name, string prompt) => new()
    {
        Id = id,
        Name = name,
        Kind = DetectionClassKind.Text,
        TextPrompt = prompt,
        Category = "Test"
    };

    [Fact]
    public async Task CompileAsync_BuildsPackFromEncoderOutput()
    {
        // Arrange
        var c1 = TextClass("c1", "Helmet", "construction worker with helmet");
        var c2 = TextClass("c2", "Vest", "high-vis safety vest");
        _classes.GetByIdAsync("c1", Arg.Any<CancellationToken>()).Returns(c1);
        _classes.GetByIdAsync("c2", Arg.Any<CancellationToken>()).Returns(c2);

        _encoder.EmbeddingDimension.Returns(512);
        _encoder.Backend.Returns("onnx-clip-vit-b32");
        var flat = new float[2 * 512];
        for (int i = 0; i < flat.Length; i++) flat[i] = i * 0.001f;
        _encoder.EncodeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(flat);

        var compiler = Build();

        // Act
        var pack = await compiler.CompileAsync("yolo-world-v2-s", ["c1", "c2"], name: "Test Pack");

        // Assert
        pack.Name.Should().Be("Test Pack");
        pack.SourceModelId.Should().Be("yolo-world-v2-s");
        pack.EncoderBackend.Should().Be("onnx-clip-vit-b32");
        pack.EmbeddingDimension.Should().Be(512);
        pack.ClassIds.Should().BeEquivalentTo(new[] { "c1", "c2" }, opts => opts.WithStrictOrdering());
        pack.Prompts.Should().BeEquivalentTo(new[]
            { "construction worker with helmet", "high-vis safety vest" }, opts => opts.WithStrictOrdering());
        pack.EmbeddingsBlob.Length.Should().Be(2 * 512 * sizeof(float));
        pack.PromptSnapshots.Should().HaveCount(2);
        pack.PromptSnapshots["c1"].Should().Be("construction worker with helmet");
        pack.Stale.Should().BeFalse();

        await _packs.Received(1).InsertAsync(Arg.Any<CompiledPromptPack>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompileAsync_AutoGeneratesName_WhenNullOrWhitespace()
    {
        var c = TextClass("c", "X", "prompt");
        _classes.GetByIdAsync("c", Arg.Any<CancellationToken>()).Returns(c);
        _encoder.EmbeddingDimension.Returns(512);
        _encoder.Backend.Returns("test-enc");
        _encoder.EncodeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new float[512]);

        var pack = await Build().CompileAsync("m1", ["c"], name: null);

        pack.Name.Should().Contain("test-enc", "auto-name zawiera backend encodera");
    }

    [Fact]
    public async Task CompileAsync_Throws_WhenClassMissing()
    {
        _classes.GetByIdAsync("missing", Arg.Any<CancellationToken>()).Returns((DetectionClass?)null);

        var act = async () => await Build().CompileAsync("m1", ["missing"]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing*nie istnieje*");
    }

    [Fact]
    public async Task CompileAsync_Throws_WhenClassKindIsClosedSetBinding()
    {
        var c = new DetectionClass
        {
            Id = "c",
            Name = "Legacy",
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = "yolo",
            ClosedSetLabel = "person"
        };
        _classes.GetByIdAsync("c", Arg.Any<CancellationToken>()).Returns(c);

        var act = async () => await Build().CompileAsync("m1", ["c"]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Text / TextAndVisual*");
    }

    [Fact]
    public async Task CompileAsync_Throws_WhenClassHasEmptyTextPrompt()
    {
        var c = new DetectionClass
        {
            Id = "c",
            Name = "Empty",
            Kind = DetectionClassKind.Text,
            TextPrompt = "   "
        };
        _classes.GetByIdAsync("c", Arg.Any<CancellationToken>()).Returns(c);

        var act = async () => await Build().CompileAsync("m1", ["c"]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*TextPrompt*");
    }

    [Fact]
    public async Task CompileAsync_Throws_WhenClassIdsEmpty()
    {
        var act = async () => await Build().CompileAsync("m1", []);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CompileAsync_Throws_WhenEncoderReturnsWrongLength()
    {
        var c = TextClass("c", "X", "prompt");
        _classes.GetByIdAsync("c", Arg.Any<CancellationToken>()).Returns(c);
        _encoder.EmbeddingDimension.Returns(512);
        _encoder.Backend.Returns("test-enc");
        _encoder.EncodeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new float[256]); // złe rozmiar — oczekujemy 512

        var act = async () => await Build().CompileAsync("m1", ["c"]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*256*oczekiwano*");
    }

    [Fact]
    public async Task RefreshStalenessAsync_MarksStale_WhenPromptChanged()
    {
        var pack = new CompiledPromptPack
        {
            Id = "p1",
            Stale = false,
            PromptSnapshots = new Dictionary<string, string>
            {
                ["c1"] = "old prompt"
            }
        };
        _packs.GetByIdAsync("p1", Arg.Any<CancellationToken>()).Returns(pack);

        var currentClass = TextClass("c1", "X", "NEW prompt changed");
        _classes.GetByIdAsync("c1", Arg.Any<CancellationToken>()).Returns(currentClass);

        var isStale = await Build().RefreshStalenessAsync("p1");

        isStale.Should().BeTrue();
        await _packs.Received(1).MarkStaleAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshStalenessAsync_ReturnsFalse_WhenPromptUnchanged()
    {
        var pack = new CompiledPromptPack
        {
            Id = "p1",
            Stale = false,
            PromptSnapshots = new Dictionary<string, string>
            {
                ["c1"] = "original prompt"
            }
        };
        _packs.GetByIdAsync("p1", Arg.Any<CancellationToken>()).Returns(pack);
        _classes.GetByIdAsync("c1", Arg.Any<CancellationToken>())
            .Returns(TextClass("c1", "X", "original prompt"));

        var isStale = await Build().RefreshStalenessAsync("p1");

        isStale.Should().BeFalse();
        await _packs.DidNotReceive().MarkStaleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
