using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Downloader;

public sealed class DownloadResult
{
    public int Ok { get; init; }
    public int Fail { get; init; }
    public IReadOnlyList<TileRequest> FailedTiles { get; init; } = [];
}
