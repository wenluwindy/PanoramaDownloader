using PanoramaDownloader.Core.Detectors;

namespace PanoramaDownloader.Tests;

public class Yun720TileInferencerTests
{
    [Fact]
    public void TryParseTileUrl_ParsesClassicPattern()
    {
        var url = "https://ssl-panoimg6.720static.com/resource/prod/a/b/521330/imgs/f/l3/01/l3_f_01_02.jpg";
        var m = Yun720TileInferencer.TryParseTileUrl(url);

        Assert.NotNull(m);
        Assert.Equal("f", m!.Face);
        Assert.Equal(3, m.Level);
        Assert.Equal(1, m.Row);
        Assert.Equal(2, m.Col);
        Assert.EndsWith("/imgs/", m.BaseUrl);
    }

    [Fact]
    public void TryParseTileUrl_StripsQueryString()
    {
        var url = "https://ssl-panoimg6.720static.com/resource/prod/a/b/521330/imgs/f/l3/01/l3_f_01_02.jpg?t=1590049635";
        var m = Yun720TileInferencer.TryParseTileUrl(url);

        Assert.NotNull(m);
        Assert.Equal(1, m!.Row);
        Assert.Equal(2, m.Col);
    }

    [Fact]
    public void InferGridExtent_ExpandsPartialHighLevelTo5()
    {
        Assert.Equal(5, Yun720TileInferencer.InferGridExtent(3, level: 3));
        Assert.Equal(5, Yun720TileInferencer.InferGridExtent(4, level: 3));
        Assert.Equal(5, Yun720TileInferencer.InferGridExtent(5, level: 3));
        Assert.Equal(6, Yun720TileInferencer.InferGridExtent(6, level: 3));
        Assert.Equal(7, Yun720TileInferencer.InferGridExtent(7, level: 3));
    }

    [Fact]
    public void BuildTileRequests_GeneratesExpectedCount()
    {
        var scene = new Core.Models.PanoramaScene
        {
            Id = "1",
            Name = "test",
            TileBaseUrl = "https://cdn.example/imgs/",
            Levels =
            [
                new Core.Models.PanoramaLevel
                {
                    Level = 3,
                    Rows = 5,
                    Cols = 5,
                    IndexPad = 2,
                    UrlTemplate = "{face}/l{level}/{rowPadded}/l{level}_{face}_{rowPadded}_{colPadded}.jpg"
                }
            ]
        };

        var tiles = Yun720TileInferencer.BuildTileRequests(scene);
        Assert.Equal(6 * 5 * 5, tiles.Count);
        Assert.Contains(tiles, t => t.Url.EndsWith("/f/l3/01/l3_f_01_01.jpg"));
    }

    [Fact]
    public void EnrichScenes_UsesPerLevelMax_AndExpandsIncomplete()
    {
        var manifest = new Core.Models.PanoramaManifest
        {
            Scenes = [new Core.Models.PanoramaScene { Id = "521330", Name = "s" }]
        };

        var entries = new List<Core.Models.CapturedNetworkEntry>
        {
            new() { Url = "https://cdn.example/resource/521330/imgs/f/l3/01/l3_f_01_01.jpg" },
            new() { Url = "https://cdn.example/resource/521330/imgs/f/l3/03/l3_f_03_04.jpg" },
            new() { Url = "https://cdn.example/resource/521330/imgs/r/l2/01/l2_r_01_02.jpg" },
        };

        Yun720TileInferencer.EnrichScenes(manifest, entries);
        var scene = manifest.Scenes[0];
        Assert.NotNull(scene.TileBaseUrl);

        var l3 = scene.Levels.First(l => l.Level == 3);
        // 只观察到 row=3,col=4 → 应补全到 5×5
        Assert.Equal(5, l3.Rows);
        Assert.Equal(5, l3.Cols);
    }
}
