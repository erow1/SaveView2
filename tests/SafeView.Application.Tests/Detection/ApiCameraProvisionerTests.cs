using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Detection;
using SafeView.Domain.Detection;
using SafeView.Domain.Zones;

namespace SafeView.Application.Tests.Detection;

public class ApiCameraProvisionerTests
{
    private readonly IRoiRepository _rois = Substitute.For<IRoiRepository>();
    private readonly IZoneRepository _zones = Substitute.For<IZoneRepository>();

    private ApiCameraProvisioner Build() =>
        new(_rois, _zones, NullLogger<ApiCameraProvisioner>.Instance);

    [Fact]
    public async Task EnsureFullFrameRoiAndZoneAsync_CreatesBoth_WhenNoneExist()
    {
        _rois.ListEnabledByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Roi>());
        _zones.ListByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Zone>());

        // Capture inserted ROI to feed back as already-existing for Zone check
        Roi? insertedRoi = null;
        _rois.InsertAsync(Arg.Do<Roi>(r => insertedRoi = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await Build().EnsureFullFrameRoiAndZoneAsync("cam1");

        await _rois.Received(1).InsertAsync(Arg.Is<Roi>(r =>
            r.IsFullFrame
            && r.CameraId == "cam1"
            && r.Rectangle.X == 0 && r.Rectangle.Y == 0
            && r.Rectangle.Width == 1 && r.Rectangle.Height == 1
            && r.ModelIds.Count == 0),
            Arg.Any<CancellationToken>());

        await _zones.Received(1).InsertAsync(Arg.Is<Zone>(z =>
            z.CameraId == "cam1"
            && z.Polygon.Count == 4
            && z.RoiId == insertedRoi!.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureFullFrameRoiAndZoneAsync_IsIdempotent_WhenBothExist()
    {
        var roi = new Roi { Id = "roi1", CameraId = "cam1", IsFullFrame = true };
        var zone = new Zone
        {
            Id = "zone1",
            CameraId = "cam1",
            RoiId = "roi1",
            Polygon =
            [
                new ZonePoint { X = 0, Y = 0 },
                new ZonePoint { X = 1, Y = 0 },
                new ZonePoint { X = 1, Y = 1 },
                new ZonePoint { X = 0, Y = 1 }
            ]
        };
        _rois.ListEnabledByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Roi> { roi });
        _zones.ListByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Zone> { zone });

        await Build().EnsureFullFrameRoiAndZoneAsync("cam1");

        await _rois.DidNotReceive().InsertAsync(Arg.Any<Roi>(), Arg.Any<CancellationToken>());
        await _zones.DidNotReceive().InsertAsync(Arg.Any<Zone>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureFullFrameRoiAndZoneAsync_CreatesOnlyZone_WhenRoiExistsButZoneMissing()
    {
        var roi = new Roi { Id = "roi1", CameraId = "cam1", IsFullFrame = true };
        _rois.ListEnabledByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Roi> { roi });
        _zones.ListByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Zone>());

        await Build().EnsureFullFrameRoiAndZoneAsync("cam1");

        await _rois.DidNotReceive().InsertAsync(Arg.Any<Roi>(), Arg.Any<CancellationToken>());
        await _zones.Received(1).InsertAsync(Arg.Is<Zone>(z => z.RoiId == "roi1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureFullFrameRoiAndZoneAsync_CreatesNewZone_WhenExistingZoneIsNotFullFrame()
    {
        // ROI istnieje pełnokadrowa, ale strefa nie pokrywa pełnego kadru — provisioner ma utworzyć nową
        var roi = new Roi { Id = "roi1", CameraId = "cam1", IsFullFrame = true };
        var partialZone = new Zone
        {
            Id = "zoneX",
            CameraId = "cam1",
            RoiId = "roi1",
            Polygon =
            [
                new ZonePoint { X = 0.1, Y = 0.1 },
                new ZonePoint { X = 0.5, Y = 0.1 },
                new ZonePoint { X = 0.5, Y = 0.5 },
                new ZonePoint { X = 0.1, Y = 0.5 }
            ]
        };
        _rois.ListEnabledByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Roi> { roi });
        _zones.ListByCameraAsync("cam1", Arg.Any<CancellationToken>()).Returns(new List<Zone> { partialZone });

        await Build().EnsureFullFrameRoiAndZoneAsync("cam1");

        await _zones.Received(1).InsertAsync(Arg.Is<Zone>(z =>
            z.RoiId == "roi1"
            && z.Polygon.Count == 4
            && z.Polygon.Any(p => p.X == 1 && p.Y == 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureFullFrameRoiAndZoneAsync_Throws_WhenCameraIdEmpty()
    {
        var act = async () => await Build().EnsureFullFrameRoiAndZoneAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
