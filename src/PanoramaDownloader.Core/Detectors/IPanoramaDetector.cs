using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Detectors;

public interface IPanoramaDetector
{
    string Name { get; }
    bool CanHandle(string sourceUrl);
    DetectionResult Detect(string sourceUrl, IReadOnlyList<CapturedNetworkEntry> entries, string? pageHtml);
}
