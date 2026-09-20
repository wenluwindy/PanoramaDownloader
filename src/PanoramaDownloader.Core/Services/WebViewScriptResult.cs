using System.Text.Json;

namespace PanoramaDownloader.Core.Services;

/// <summary>
/// WebView2 ExecuteScriptAsync 返回值可能是 JSON 字符串或 JSON 对象，需统一解析。
/// </summary>
public static class WebViewScriptResult
{
    public static string? ToText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null" || raw == "undefined")
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => doc.RootElement.GetRawText(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    => doc.RootElement.GetRawText(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return raw.Trim().Trim('"');
        }
    }
}
