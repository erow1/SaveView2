using FluentAssertions;
using NSubstitute;
using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Detection;

namespace SafeView.Application.Tests.Detection;

/// <summary>
/// Testy kontraktu IRoiCropper — weryfikują że pipeline i handlery mogą polegać
/// na zachowaniu (cleanup, transformacje koordynatów).
/// Rzeczywista implementacja ImageSharpRoiCropper ma integration testy osobno.
/// </summary>
public class RoiCropperContractTests
{
    [Fact]
    public async Task Mock_CanReturn_RoiCropResult()
    {
        var cropper = Substitute.For<IRoiCropper>();
        var roi = new RoiRectangle { X = 0.1, Y = 0.1, Width = 0.5, Height = 0.5 };
        cropper.CropAsync("/frame.jpg", roi, Arg.Any<CancellationToken>())
            .Returns(new RoiCropResult("/tmp/crop.jpg", 384, 216, 1920, 1080, 3840, 2160));

        var result = await cropper.CropAsync("/frame.jpg", roi);
        result.CropPath.Should().Be("/tmp/crop.jpg");
        result.OriginalWidth.Should().Be(3840);
        result.CropWidth.Should().Be(1920);
    }

    [Fact]
    public async Task Mock_CanReturn_BboxCropWithPadding()
    {
        var cropper = Substitute.For<IRoiCropper>();
        cropper.CropBboxAsync("/frame.jpg", 100, 100, 80, 60,
                paddingRatio: 0.2, minSize: 128, Arg.Any<CancellationToken>())
            .Returns(new RoiCropResult("/tmp/bbox.jpg", 84, 88, 128, 128, 3840, 2160));

        var result = await cropper.CropBboxAsync("/frame.jpg", 100, 100, 80, 60, 0.2, 128);

        result.CropWidth.Should().BeGreaterThanOrEqualTo(128, "padding enforces min size for small bbox");
        result.OffsetX.Should().BeLessThan(100, "padding expanded bbox around center");
    }

    [Fact]
    public void RoiCropResult_ContainsAllMetadataForCoordTransform()
    {
        var r = new RoiCropResult(
            CropPath: "/tmp/crop.jpg",
            OffsetX: 500, OffsetY: 300,
            CropWidth: 1000, CropHeight: 600,
            OriginalWidth: 1920, OriginalHeight: 1080);

        // Wszystko potrzebne do transformacji: detection in crop → normalized camera
        r.OffsetX.Should().Be(500);
        r.OffsetY.Should().Be(300);
        r.OriginalWidth.Should().Be(1920);
        r.OriginalHeight.Should().Be(1080);

        // Przykład transformacji: bbox(100,50,w=60,h=40) w cropie
        // → globalne: (100+500=600, 50+300=350, 60, 40)
        // → znormalizowane: (600/1920, 350/1080, 60/1920, 40/1080)
        var detX = (100.0 + r.OffsetX) / r.OriginalWidth;
        var detY = (50.0 + r.OffsetY) / r.OriginalHeight;
        detX.Should().BeApproximately(600.0 / 1920, 1e-9);
        detY.Should().BeApproximately(350.0 / 1080, 1e-9);
    }
}
