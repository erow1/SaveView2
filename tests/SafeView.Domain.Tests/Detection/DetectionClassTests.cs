using FluentAssertions;
using SafeView.Domain.Detection;
using SafeView.Domain.ML;

namespace SafeView.Domain.Tests.Detection;

public class DetectionClassTests
{
    [Fact]
    public void RequiredCapabilities_ClosedSetBinding_MapsToClosedSet()
    {
        var klass = new DetectionClass { Kind = DetectionClassKind.ClosedSetBinding };
        klass.RequiredCapabilities.Should().Be(ModelCapabilities.ClosedSet);
    }

    [Fact]
    public void RequiredCapabilities_Text_MapsToTextPrompts()
    {
        var klass = new DetectionClass { Kind = DetectionClassKind.Text };
        klass.RequiredCapabilities.Should().Be(ModelCapabilities.TextPrompts);
    }

    [Fact]
    public void RequiredCapabilities_Visual_MapsToVisualPrompts()
    {
        var klass = new DetectionClass { Kind = DetectionClassKind.Visual };
        klass.RequiredCapabilities.Should().Be(ModelCapabilities.VisualPrompts);
    }

    [Fact]
    public void RequiredCapabilities_TextAndVisual_MapsToBothFlags()
    {
        var klass = new DetectionClass { Kind = DetectionClassKind.TextAndVisual };
        klass.RequiredCapabilities
            .Should().Be(ModelCapabilities.TextPrompts | ModelCapabilities.VisualPrompts);
    }

    [Fact]
    public void ModelCapabilities_FlagsCompose_AsExpected()
    {
        var caps = ModelCapabilities.ClosedSet
                   | ModelCapabilities.TextPrompts
                   | ModelCapabilities.VisualPrompts;

        caps.HasFlag(ModelCapabilities.ClosedSet).Should().BeTrue();
        caps.HasFlag(ModelCapabilities.TextPrompts).Should().BeTrue();
        caps.HasFlag(ModelCapabilities.VisualPrompts).Should().BeTrue();
    }

    [Fact]
    public void ModelCapabilities_Default_IsClosedSetForBackwardCompat()
    {
        // Istniejące modele (classical YOLO) muszą mieć kapabilitet ClosedSet domyślnie —
        // bez tego migracja "starych" triggerów nie działa bez ingerencji usera.
        var model = new MLModel();
        model.Capabilities.Should().Be(ModelCapabilities.ClosedSet);
    }
}
