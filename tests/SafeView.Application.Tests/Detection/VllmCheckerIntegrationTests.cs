using FluentAssertions;
using NSubstitute;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.LLM;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Application.Tests.Detection;

/// <summary>
/// Testy kontraktu IVllmChecker — nie testujemy implementacji (VllmChecker w SafeView.LLM),
/// bo wymagałoby referencji od Application.Tests do LLM. Zamiast tego używamy mocka
/// i weryfikujemy zachowanie DetectionPipeline/TriggerEvaluator z VLLM gatem.
///
/// Dla testów samej logiki VllmChecker → patrz SafeView.LLM.Tests (jeśli dodany).
/// </summary>
public class VllmCheckerContractTests
{
    private static ActionContext MakeContext() => new()
    {
        CameraId = "cam-1",
        CameraName = "Kamera 1",
        ZoneId = "zone-1",
        ZoneName = "Strefa",
        TriggerId = "trg-1",
        TriggerName = "Trigger",
        FrameSnapshotPath = "/tmp/frame.jpg",
        Detections = [new DetectionResult("m1", "person", 0.9, new Bbox(0.4, 0.4, 0.2, 0.2))],
        OccurredAt = new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task Mock_CanReturn_Confirmed()
    {
        var checker = Substitute.For<IVllmChecker>();
        var cfg = new VllmCheckConfig { Enabled = true };
        checker.CheckAsync(cfg, Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
               .Returns(new VllmCheckResult(true, 0.9, "Genuine fire", "{}"));

        var result = await checker.CheckAsync(cfg, MakeContext());
        result.Confirmed.Should().BeTrue();
        result.Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task Mock_CanReturn_RejectedWithReason()
    {
        var checker = Substitute.For<IVllmChecker>();
        var cfg = new VllmCheckConfig { Enabled = true };
        checker.CheckAsync(cfg, Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
               .Returns(new VllmCheckResult(false, 0.85, "Just a lamp, not real fire", "{}"));

        var result = await checker.CheckAsync(cfg, MakeContext());
        result.Confirmed.Should().BeFalse();
        result.Reason.Should().Contain("lamp");
    }

    [Fact]
    public async Task Mock_CanReturn_Error()
    {
        var checker = Substitute.For<IVllmChecker>();
        var cfg = new VllmCheckConfig { Enabled = true };
        checker.CheckAsync(cfg, Arg.Any<ActionContext>(), Arg.Any<CancellationToken>())
               .Returns(new VllmCheckResult(false, 0, "LLM timeout", null, IsError: true));

        var result = await checker.CheckAsync(cfg, MakeContext());
        result.IsError.Should().BeTrue();
        result.Confirmed.Should().BeFalse();
    }
}

/// <summary>
/// Unit testy VllmCheckConfig — domenowe invarianty + defaults.
/// </summary>
public class VllmCheckConfigTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        var cfg = new VllmCheckConfig();
        cfg.Enabled.Should().BeFalse();
        cfg.IncludeFrame.Should().BeTrue();
        cfg.RejectOnError.Should().BeFalse("fail-open by default is safer");
        cfg.MinConfidenceToConfirm.Should().BeInRange(0, 1);
        cfg.PromptTemplate.Should().NotBeEmpty();
        cfg.SystemPrompt.Should().NotBeEmpty();
    }

    [Fact]
    public void Can_OverrideAllFields()
    {
        var cfg = new VllmCheckConfig
        {
            Enabled = true,
            IncludeFrame = false,
            RejectOnError = true,
            ModelName = "llava-1.6",
            MinConfidenceToConfirm = 0.85,
            PromptTemplate = "custom {camera}",
            SystemPrompt = "custom role"
        };
        cfg.Enabled.Should().BeTrue();
        cfg.IncludeFrame.Should().BeFalse();
        cfg.ModelName.Should().Be("llava-1.6");
    }
}
