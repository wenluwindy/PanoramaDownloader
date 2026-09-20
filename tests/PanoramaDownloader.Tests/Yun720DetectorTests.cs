using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Tests;

public class Yun720DetectorTests
{
    private readonly Yun720Detector _detector = new();

    [Fact]
    public void CanHandle_Accepts720YunUrl()
    {
        Assert.True(_detector.CanHandle("https://www.720yun.com/t/abc123"));
        Assert.False(_detector.CanHandle("https://example.com/pano"));
    }

    [Fact]
    public void Detect_ReturnsPlaceholderScene_WhenNoNetworkData()
    {
        var result = _detector.Detect(
            "https://www.720yun.com/t/demoWork",
            [],
            "<html><head><title>示例全景</title></head><body>krpano player</body></html>");

        Assert.True(result.Success);
        Assert.False(result.NeedLogin);
        Assert.NotNull(result.Manifest);
        Assert.Equal("yun720", result.Manifest!.Detector);
        Assert.NotEmpty(result.Manifest.Scenes);
        Assert.Contains(result.Manifest.Scenes, s => s.Name.Contains("示例") || s.Id.Contains("demoWork"));
    }

    [Fact]
    public void Detect_ParsesScenesFromJsonBody()
    {
        var entries = new List<CapturedNetworkEntry>
        {
            new()
            {
                Url = "https://ssl-api.720yun.com/api/work/detail",
                StatusCode = 200,
                ContentType = "application/json",
                BodyText = """{"data":{"scenes":[{"id":"s1","name":"大厅","thumb":"https://cdn.example/a.jpg"},{"id":"s2","name":"走廊","thumbnail":"https://cdn.example/b.jpg"}]}}"""
            }
        };

        var result = _detector.Detect("https://www.720yun.com/t/xxx", entries, null);

        Assert.True(result.Success);
        Assert.False(result.NeedLogin);
        Assert.Equal(2, result.Manifest!.Scenes.Count);
        Assert.Equal("大厅", result.Manifest.Scenes[0].Name);
    }

    [Fact]
    public void Detect_PublicWork_IgnoresIncidentalUserApi401()
    {
        var entries = new List<CapturedNetworkEntry>
        {
            new()
            {
                Url = "https://ssl-api.720yun.com/api/user/profile",
                StatusCode = 401,
                BodyText = """{"code":401,"message":"未登录"}"""
            },
            new()
            {
                Url = "https://cdn.720static.com/pano/tiles/l/0/0.jpg",
                StatusCode = 200
            }
        };

        var result = _detector.Detect("https://www.720yun.com/t/public", entries, "<html><title>公开全景</title><body>请登录</body></html>");

        Assert.True(result.Success);
        Assert.False(result.NeedLogin);
        Assert.NotNull(result.Manifest);
    }

    [Fact]
    public void Detect_FlagsNeedLogin_OnlyWhenWorkApiDeniedAndNoResources()
    {
        var entries = new List<CapturedNetworkEntry>
        {
            new()
            {
                Url = "https://ssl-api.720yun.com/api/work/detail",
                StatusCode = 401,
                BodyText = """{"code":401,"message":"未登录"}"""
            }
        };

        var result = _detector.Detect("https://www.720yun.com/t/private", entries, null);

        Assert.True(result.NeedLogin);
        Assert.False(result.Success);
    }
}
