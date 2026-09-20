using PanoramaDownloader.Core.Services;

namespace PanoramaDownloader.Tests;

public class WebViewScriptResultTests
{
    [Fact]
    public void ToText_ParsesJsonStringLiteral()
    {
        var raw = "\"{\\\"scenes\\\":[]}\"";
        var text = WebViewScriptResult.ToText(raw);
        Assert.Equal("{\"scenes\":[]}", text);
    }

    [Fact]
    public void ToText_ParsesJsonObjectDirectly()
    {
        var raw = "{\"scenes\":[{\"id\":\"1\"}],\"images\":[]}";
        var text = WebViewScriptResult.ToText(raw);
        Assert.Contains("\"scenes\"", text);
        Assert.Contains("\"id\":\"1\"", text);
    }

    [Fact]
    public void ToText_ReturnsNull_ForNullLiteral()
    {
        Assert.Null(WebViewScriptResult.ToText("null"));
    }
}
