namespace PanoramaDownloader.Core.Models;

public enum AnalysisStatus
{
    Idle,
    Loading,
    NeedLogin,
    Analyzing,
    Succeeded,
    Failed
}

public sealed class CapturedNetworkEntry
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? BodyText { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class DetectionResult
{
    public bool Success { get; set; }
    public bool NeedLogin { get; set; }
    public string? Message { get; set; }
    public PanoramaManifest? Manifest { get; set; }
}
