namespace PanoramaDownloader.Core.Stitcher;

/// <summary>
/// 等距柱状投影调整参数（预览与最终导出共用）。
/// </summary>
public sealed class EquirectProjectOptions
{
    /// <summary>水平偏航（度），正值向右旋转视角 / 图像内容左移。</summary>
    public double YawDegrees { get; set; }

    /// <summary>俯仰（度）。</summary>
    public double PitchDegrees { get; set; }

    /// <summary>滚转（度）。</summary>
    public double RollDegrees { get; set; }

    /// <summary>上下面额外旋转 180°（720 云常见）。</summary>
    public bool RotateUpDown180 { get; set; } = true;

    /// <summary>
    /// 逻辑面 → 源文件面。例如 {"f":"b"} 表示用 b.jpg 作为前面。
    /// 缺省时恒等映射。
    /// </summary>
    public Dictionary<string, string> FaceSourceMap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["f"] = "f",
            ["r"] = "r",
            ["b"] = "b",
            ["l"] = "l",
            ["u"] = "u",
            ["d"] = "d"
        };

    /// <summary>各源面顺时针旋转角度：0/90/180/270。</summary>
    public Dictionary<string, int> FaceRotations { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["f"] = 0,
            ["r"] = 0,
            ["b"] = 0,
            ["l"] = 0,
            ["u"] = 0,
            ["d"] = 0
        };

    public int JpegQuality { get; set; } = 92;

    public EquirectProjectOptions Clone()
    {
        return new EquirectProjectOptions
        {
            YawDegrees = YawDegrees,
            PitchDegrees = PitchDegrees,
            RollDegrees = RollDegrees,
            RotateUpDown180 = RotateUpDown180,
            JpegQuality = JpegQuality,
            FaceSourceMap = new Dictionary<string, string>(FaceSourceMap, StringComparer.OrdinalIgnoreCase),
            FaceRotations = new Dictionary<string, int>(FaceRotations, StringComparer.OrdinalIgnoreCase)
        };
    }
}
