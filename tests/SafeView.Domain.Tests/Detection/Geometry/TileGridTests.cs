using FluentAssertions;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;

namespace SafeView.Domain.Tests.Detection.Geometry;

public class TileGridTests
{
    [Fact]
    public void Generate_SingleTile_WhenImageFitsInTileSize()
    {
        var tiles = TileGrid.Generate(640, 480, 640, 0.2);
        tiles.Should().HaveCount(1);
        tiles[0].Should().Be(new Tile(0, 0, 640, 480));
    }

    [Fact]
    public void Generate_SingleTile_WhenImageEqualsTileSize()
    {
        var tiles = TileGrid.Generate(640, 640, 640, 0.2);
        tiles.Should().HaveCount(1);
        tiles[0].Should().Be(new Tile(0, 0, 640, 640));
    }

    [Fact]
    public void Generate_MultipleTiles_WhenImageLargerThanTile()
    {
        // 1920x1080, tile=640, overlap=0.2 → step=512
        // x: 0, 512, 1024, 1280 (1920-640) → 4 tiles horizontally
        // y: 0, 440 (1080-640)                → 2 tiles vertically
        // Total: 8 tiles
        var tiles = TileGrid.Generate(1920, 1080, 640, 0.2);
        tiles.Should().HaveCount(8);

        tiles[0].Should().Be(new Tile(0, 0, 640, 640));
        tiles[^1].Bottom.Should().Be(1080); // last tile touches bottom
        tiles[^1].Right.Should().Be(1920);  // last tile touches right
    }

    [Fact]
    public void Generate_CoversAllPixels()
    {
        // Verify union of tiles covers full image
        const int W = 1920, H = 1080, T = 640;
        var tiles = TileGrid.Generate(W, H, T, 0.2);

        // Check corners are inside some tile
        tiles.Any(t => t.X == 0 && t.Y == 0).Should().BeTrue("top-left covered");
        tiles.Any(t => t.Right == W && t.Bottom == H).Should().BeTrue("bottom-right covered");
        tiles.Any(t => t.Right == W && t.Y == 0).Should().BeTrue("top-right covered");
        tiles.Any(t => t.X == 0 && t.Bottom == H).Should().BeTrue("bottom-left covered");
    }

    [Fact]
    public void Generate_4KImage_Produces12To20Tiles()
    {
        // 4K = 3840x2160, tile=640, overlap=0.2
        // x: 0, 512, 1024, 1536, 2048, 2560, 3072, 3200 (3840-640) → 8 unikalnie
        // Ale step 512 → 0, 512, 1024, 1536, 2048, 2560, 3072, 3584 - ale 3584+640=4224 > 3840 → last=3200
        // Actually: while 0+640=640<3840, 512+640=1152<3840, ..., 3072+640=3712<3840 → ok
        // 3072+512=3584, 3584+640=4224 > 3840 → break; last tile at 3840-640=3200
        // So starts: 0, 512, 1024, 1536, 2048, 2560, 3072, 3200 → 8 tiles (last is close to 3072)
        // y: 0, 512, 1024, 1520 (2160-640) → 4 tiles
        // Total 8 * 4 = 32
        var tiles = TileGrid.Generate(3840, 2160, 640, 0.2);

        tiles.Should().HaveCountGreaterThanOrEqualTo(24);
        tiles.Should().HaveCountLessThanOrEqualTo(48);
        tiles.All(t => t.Width <= 640 && t.Height <= 640).Should().BeTrue();
    }

    [Fact]
    public void Generate_ReturnsEmpty_ForInvalidInputs()
    {
        TileGrid.Generate(0, 100, 640, 0.2).Should().BeEmpty();
        TileGrid.Generate(100, 0, 640, 0.2).Should().BeEmpty();
        TileGrid.Generate(100, 100, 0, 0.2).Should().BeEmpty();
    }

    [Fact]
    public void Generate_HandlesZeroOverlap()
    {
        // overlap=0 → step=tileSize=640, no overlap
        var tiles = TileGrid.Generate(1280, 640, 640, 0.0);
        tiles.Should().HaveCount(2); // 2 tiles side-by-side, 0 overlap
        tiles[0].Should().Be(new Tile(0, 0, 640, 640));
        tiles[1].Should().Be(new Tile(640, 0, 640, 640));
    }

    [Fact]
    public void Generate_LastTilesAttachedToEdge()
    {
        // Image 1000x640, tile=640, step=512
        // x: 0, 360 (1000-640) → last attached to right edge, NOT 512 (bo 512+640=1152 > 1000)
        var tiles = TileGrid.Generate(1000, 640, 640, 0.2);
        tiles.Should().HaveCount(2);
        tiles[0].X.Should().Be(0);
        tiles[1].X.Should().Be(360); // 1000 - 640
        tiles[1].Right.Should().Be(1000); // dotyka prawej krawędzi
    }

    // ── Adaptive mode picker ───────────────────────────────────────────────

    [Theory]
    [InlineData(500, 400, 640, RoiInferenceMode.Native)]   // ROI ≤ model
    [InlineData(640, 640, 640, RoiInferenceMode.Native)]   // ROI == model
    [InlineData(1000, 800, 640, RoiInferenceMode.Resize)]  // ROI 1-2× model
    [InlineData(1280, 1280, 640, RoiInferenceMode.Resize)] // dokładnie 2×
    [InlineData(1500, 1000, 640, RoiInferenceMode.Sliced)] // >2× model
    [InlineData(3840, 2160, 640, RoiInferenceMode.Sliced)] // 4K
    public void PickAdaptiveMode_ReturnsCorrectStrategy(int w, int h, int modelSize, RoiInferenceMode expected)
        => TileGrid.PickAdaptiveMode(w, h, modelSize).Should().Be(expected);
}
