using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Tests;

public class Yun720ConfigExtractorTests
{
    [Fact]
    public void NormalizeImgsBase_FromFullTileUrl()
    {
        var url = "https://ssl-panoimg6.720static.com/resource/prod/a/b/521330/imgs/f/l3/01/l3_f_01_02.jpg";
        var bas = Yun720ConfigExtractor.NormalizeImgsBase(url);
        Assert.Equal("https://ssl-panoimg6.720static.com/resource/prod/a/b/521330/imgs/", bas);
    }

    [Fact]
    public void EnrichManifest_ReadsScenesAndImgsPathFromApiJson()
    {
        var manifest = new PanoramaManifest
        {
            Title = "720 云作品",
            Scenes = []
        };

        var entries = new List<CapturedNetworkEntry>
        {
            new()
            {
                Url = "https://ssl-api.720yun.com/api/product/detail",
                StatusCode = 200,
                BodyText = """
                {
                  "data": {
                    "name": "样板间漫游",
                    "scenes": [
                      {
                        "id": "640863",
                        "name": "客厅",
                        "thumbUrl": "https://cdn.example/thumb1.jpg",
                        "path": "https://ssl-panoimg6.720static.com/resource/prod/a/b/521330/imgs/"
                      },
                      {
                        "id": "640864",
                        "name": "卧室",
                        "cover": "https://cdn.example/thumb2.jpg",
                        "imgPath": "https://ssl-panoimg6.720static.com/resource/prod/a/b/521331/imgs/"
                      }
                    ]
                  }
                }
                """
            }
        };

        Yun720ConfigExtractor.EnrichManifest(manifest, entries, null);

        Assert.Equal(2, manifest.Scenes.Count);
        Assert.Equal("客厅", manifest.Scenes[0].Name);
        Assert.Contains("/521330/imgs/", manifest.Scenes[0].TileBaseUrl);
        Assert.Contains("/521331/imgs/", manifest.Scenes[1].TileBaseUrl);
    }
}

public class Yun720MatrixTileTests
{
    [Fact]
    public void TryParseTileUrl_ParsesMatrixPattern()
    {
        var url = "https://ssl-panoimg31.720static.com/resource/matrix/bb1/09f/1338/imgs/l6/18/l6_18_64.jpg?t=1";
        var m = Yun720TileInferencer.TryParseTileUrl(url);

        Assert.NotNull(m);
        Assert.Equal(Yun720TileInferencer.TileKind.Matrix, m!.Kind);
        Assert.Equal(6, m.Level);
        Assert.Equal(18, m.Row);
        Assert.Equal(64, m.Col);
        Assert.EndsWith("/imgs/", m.BaseUrl);
        Assert.Equal(string.Empty, m.Face);
    }

    [Fact]
    public void EnrichScenes_BuildsMatrixLevels()
    {
        var manifest = new PanoramaManifest
        {
            Scenes = [new PanoramaScene { Id = "1", Name = "matrix" }]
        };
        var entries = new List<CapturedNetworkEntry>
        {
            new()
            {
                Url = "https://ssl-panoimg31.720static.com/resource/matrix/a/b/1338/imgs/l6/03/l6_03_10.jpg",
                StatusCode = 200
            },
            new()
            {
                Url = "https://ssl-panoimg31.720static.com/resource/matrix/a/b/1338/imgs/l6/18/l6_18_64.jpg",
                StatusCode = 200
            }
        };

        Yun720TileInferencer.EnrichScenes(manifest, entries);

        var scene = manifest.Scenes[0];
        Assert.Equal("matrix-multires", scene.Projection);
        Assert.Empty(scene.Faces);
        Assert.NotEmpty(scene.Levels);
        var level = scene.Levels.OrderByDescending(l => l.Level).First();
        Assert.Equal(6, level.Level);
        Assert.True(level.Rows >= 18);
        Assert.True(level.Cols >= 64);
        Assert.DoesNotContain("{face}", level.UrlTemplate);
    }

    [Fact]
    public void EnrichScenes_AppliesDefaultLevels_WhenOnlyConfigBase()
    {
        var manifest = new PanoramaManifest
        {
            Scenes =
            [
                new PanoramaScene
                {
                    Id = "s1",
                    Name = "大厅",
                    TileBaseUrl = "https://ssl-panoimg6.720static.com/resource/prod/a/b/521330/imgs/"
                }
            ]
        };

        Yun720TileInferencer.EnrichScenes(manifest, []);

        Assert.Equal("cube-multires", manifest.Scenes[0].Projection);
        Assert.Contains(manifest.Scenes[0].Levels, l => l.Level == 3 && l.Rows == 5);
    }
}
