namespace PanoramaDownloader.Core.Models;

public sealed class PanoramaManifest
{
    public string SourceUrl { get; set; } = string.Empty;
    public string Detector { get; set; } = "yun720";
    public string Title { get; set; } = string.Empty;
    public List<PanoramaScene> Scenes { get; set; } = [];
    public Dictionary<string, string> HeadersHint { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class PanoramaScene
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Projection { get; set; } = "cube-multires";
    public string? ThumbnailUrl { get; set; }
    public List<PanoramaLevel> Levels { get; set; } = [];
    public List<string> Faces { get; set; } = ["f", "r", "b", "l", "u", "d"];
    /// <summary>瓦片根路径，例如 https://cdn.../imgs/</summary>
    public string? TileBaseUrl { get; set; }
}

public sealed class PanoramaLevel
{
    public int Level { get; set; }
    public int TileSize { get; set; }
    public int FaceSize { get; set; }
    public int Rows { get; set; } = 5;
    public int Cols { get; set; } = 5;
    public int IndexPad { get; set; } = 2;
    /// <summary>
    /// 模板占位符：{face} {level} {row} {col} {rowPadded} {colPadded}
    /// 例：{face}/l{level}/{rowPadded}/l{level}_{face}_{rowPadded}_{colPadded}.jpg
    /// </summary>
    public string UrlTemplate { get; set; } = string.Empty;
}

public sealed class TileRequest
{
    public string Url { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Face { get; set; } = string.Empty;
    public int Level { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
}

public enum ExportMode
{
    Tiles,
    Equirect,
    Both
}

public sealed class ExportResult
{
    public bool Success { get; set; }
    public string? OutputDirectory { get; set; }
    public string? EquirectPath { get; set; }
    public string? Message { get; set; }
    public int DownloadedTiles { get; set; }
    public int FailedTiles { get; set; }
    public IReadOnlyList<string> FailedTileUrls { get; set; } = [];
    /// <summary>已拼接的六面路径，供整图预览调整使用。</summary>
    public Dictionary<string, string>? FacePaths { get; set; }
    /// <summary>立方体导出时需打开预览窗口；矩阵整图已直接落盘则为 false。</summary>
    public bool NeedsEquirectPreview { get; set; }
    public int SuggestedEquirectWidth { get; set; } = 4096;
    public string? Warning { get; set; }
}
