using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Services;

public sealed class AnalysisService
{
    private readonly IPanoramaDetector _detector = new Yun720Detector();

    public DetectionResult Analyze(string sourceUrl, IReadOnlyList<CapturedNetworkEntry> entries, string? pageHtml)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return new DetectionResult
            {
                Success = false,
                Message = "请输入 720 云作品 URL。"
            };
        }

        if (!_detector.CanHandle(sourceUrl))
        {
            return new DetectionResult
            {
                Success = false,
                Message = "一期仅支持 720 云链接，请检查 URL 是否正确。"
            };
        }

        return _detector.Detect(sourceUrl.Trim(), entries, pageHtml);
    }
}
