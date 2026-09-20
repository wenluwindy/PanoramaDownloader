using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Tests;

public class MultiSceneTileBindingTests
{
    [Fact]
    public void EnrichScenes_DoesNotAssignSameGroupToEveryScene()
    {
        var manifest = new PanoramaManifest
        {
            Scenes =
            [
                new PanoramaScene
                {
                    Id = "s1",
                    Name = "客厅",
                    TileBaseUrl = "https://cdn.example/resource/prod/a/b/111/imgs/"
                },
                new PanoramaScene
                {
                    Id = "s2",
                    Name = "卧室",
                    TileBaseUrl = "https://cdn.example/resource/prod/a/b/222/imgs/"
                }
            ]
        };

        // 网络只观测到客厅瓦片（常见：用户还没切换场景）
        var entries = new List<CapturedNetworkEntry>
        {
            new()
            {
                Url = "https://cdn.example/resource/prod/a/b/111/imgs/f/l3/01/l3_f_01_01.jpg",
                StatusCode = 200
            },
            new()
            {
                Url = "https://cdn.example/resource/prod/a/b/111/imgs/f/l3/02/l3_f_02_02.jpg",
                StatusCode = 200
            }
        };

        Yun720TileInferencer.EnrichScenes(manifest, entries);

        Assert.Contains("/111/imgs", manifest.Scenes[0].TileBaseUrl);
        Assert.Contains("/222/imgs", manifest.Scenes[1].TileBaseUrl);
        Assert.False(string.Equals(
            manifest.Scenes[0].TileBaseUrl!.TrimEnd('/'),
            manifest.Scenes[1].TileBaseUrl!.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnrichScenes_BindsDistinctObservedGroupsToMatchingScenes()
    {
        var manifest = new PanoramaManifest
        {
            Scenes =
            [
                new PanoramaScene { Id = "s1", Name = "A", TileBaseUrl = "https://cdn.example/r/111/imgs/" },
                new PanoramaScene { Id = "s2", Name = "B", TileBaseUrl = "https://cdn.example/r/222/imgs/" }
            ]
        };

        var entries = new List<CapturedNetworkEntry>
        {
            new() { Url = "https://cdn.example/r/111/imgs/f/l3/01/l3_f_01_01.jpg", StatusCode = 200 },
            new() { Url = "https://cdn.example/r/222/imgs/f/l3/01/l3_f_01_01.jpg", StatusCode = 200 }
        };

        Yun720TileInferencer.EnrichScenes(manifest, entries);

        Assert.Contains("/111/imgs", manifest.Scenes[0].TileBaseUrl);
        Assert.Contains("/222/imgs", manifest.Scenes[1].TileBaseUrl);
        Assert.NotEmpty(manifest.Scenes[0].Levels);
        Assert.NotEmpty(manifest.Scenes[1].Levels);
    }
}
