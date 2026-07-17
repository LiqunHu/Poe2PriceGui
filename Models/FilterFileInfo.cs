namespace Poe2PriceGui.Models;

/// <summary>
/// 可供选择的过滤器文件信息。
/// </summary>
public class FilterFileInfo
{
    /// <summary>完整文件路径。</summary>
    public string FilePath { get; set; } = "";

    /// <summary>显示名称（不含 .filter 扩展名）。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>文件来源目录标签，例如 "程序内置" 或 "用户目录"。</summary>
    public string SourceLabel { get; set; } = "";

    /// <summary>是否为程序内置过滤器。</summary>
    public bool IsBuiltIn { get; set; }
}
