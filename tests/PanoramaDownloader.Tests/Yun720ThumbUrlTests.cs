using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Models;
using PanoramaDownloader.Core.Services;

namespace PanoramaDownloader.Tests;

public class Yun720ThumbUrlTests
{
    [Fact]
    public void ExtractResourceId_FromThumbUrl()
    {
        var thumb =
            "https://thumb-t.720static.com/resource/prod/b51if9b2281/0142fwfOwtr/93539831/imgs/thumb.jpg?imageMogr2/auto-orient/thumbnail/200x200";
        Assert.Equal("93539831", Yun720ThumbUrl.ExtractResourceId(thumb));
    }

    [Fact]
    public void DeriveTileBase_RewritesThumbHost()
    {
        var thumb =
            "https://thumb-t.720static.com/resource/prod/b51if9b2281/0142fwfOwtr/93539831/imgs/thumb.jpg?imageMogr2/auto-orient/thumbnail/200x200";
        var bas = Yun720ThumbUrl.DeriveTileBase(thumb);
        Assert.NotNull(bas);
        Assert.Contains("/93539831/imgs/", bas);
        Assert.StartsWith("https://ssl-panoimg.720static.com/", bas);
        Assert.DoesNotContain("thumb", bas);
    }

    [Fact]
    public void DeriveTileBase_PrefersObservedPanoHost()
    {
        var thumb =
            "https://thumb-t.720static.com/resource/prod/a/b/93539831/imgs/thumb.jpg";
        var entries = new List<CapturedNetworkEntry>
        {
            new()
            {
                Url = "https://ssl-panoimg6.720static.com/resource/prod/a/b/111/imgs/f/l3/01/l3_f_01_01.jpg",
                StatusCode = 200
            }
        };

        var bas = Yun720ThumbUrl.DeriveTileBase(thumb, entries);
        Assert.Equal("https://ssl-panoimg6.720static.com/resource/prod/a/b/93539831/imgs/", bas);
    }

    [Fact]
    public void ApplyPageMeta_RebuildsFromItemsWpMenu()
    {
        var manifest = new PanoramaManifest
        {
            Scenes =
            [
                new PanoramaScene { Id = "only_first", Name = "旧场景" }
            ]
        };

        var meta = """
        {
          "source": "itemsWp",
          "menuCount": 2,
          "scenes": [
            {
              "id": "93539831",
              "name": "HERMES",
              "thumbUrl": "https://thumb-t.720static.com/resource/prod/a/b/93539831/imgs/thumb.jpg",
              "path": "https://thumb-t.720static.com/resource/prod/a/b/93539831/imgs/thumb.jpg",
              "source": "itemsWp"
            },
            {
              "id": "93539832",
              "name": "CHANEL",
              "thumbUrl": "https://thumb-t.720static.com/resource/prod/a/b/93539832/imgs/thumb.jpg",
              "path": "https://thumb-t.720static.com/resource/prod/a/b/93539832/imgs/thumb.jpg",
              "source": "itemsWp"
            }
          ]
        }
        """;

        SceneThumbnailEnricher.ApplyPageMeta(manifest, meta, []);

        Assert.Equal(2, manifest.Scenes.Count);
        Assert.Equal("HERMES", manifest.Scenes[0].Name);
        Assert.Equal("CHANEL", manifest.Scenes[1].Name);
        Assert.Contains("thumb.jpg", manifest.Scenes[0].ThumbnailUrl);
        Assert.Contains("/93539831/imgs/", manifest.Scenes[0].TileBaseUrl);
        Assert.Contains("/93539832/imgs/", manifest.Scenes[1].TileBaseUrl);
    }
}
