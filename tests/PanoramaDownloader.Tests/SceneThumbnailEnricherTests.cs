using PanoramaDownloader.Core.Models;
using PanoramaDownloader.Core.Services;

namespace PanoramaDownloader.Tests;

public class SceneThumbnailEnricherTests
{
    [Fact]
    public void ApplyPageMeta_FillsMissingThumbnails_FromMetaAndNetwork()
    {
        var manifest = new PanoramaManifest
        {
            Scenes =
            [
                new PanoramaScene { Id = "1", Name = "大厅" },
                new PanoramaScene { Id = "2", Name = "走廊" }
            ]
        };

        var meta = """
                   {"scenes":[{"id":"1","name":"大厅","thumbUrl":"https://cdn.example/a.jpg"}],"images":[{"src":"https://cdn.example/b.jpg"}]}
                   """;

        var entries = new List<CapturedNetworkEntry>
        {
            new() { Url = "https://cdn.example/cover_preview.jpg", StatusCode = 200 }
        };

        SceneThumbnailEnricher.ApplyPageMeta(manifest, meta, entries);

        Assert.Equal("https://cdn.example/a.jpg", manifest.Scenes[0].ThumbnailUrl);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Scenes[1].ThumbnailUrl));
    }

    [Fact]
    public void ApplyPageMeta_NormalizesProtocolRelativeUrl()
    {
        var manifest = new PanoramaManifest
        {
            Scenes = [new PanoramaScene { Id = "1", Name = "A", ThumbnailUrl = "//cdn.example/t.jpg" }]
        };

        SceneThumbnailEnricher.ApplyPageMeta(manifest, null, []);

        Assert.Equal("https://cdn.example/t.jpg", manifest.Scenes[0].ThumbnailUrl);
    }
}
