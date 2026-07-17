using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 解析和保存 POE2 过滤器文件（.filter）。
/// </summary>
public static class LootFilterService
{
    private static readonly Regex BlockStartRegex = new(@"^(Show|Hide)\s*(?:#\s*(.*))?$", RegexOptions.Compiled);
    private static readonly Regex ColorRegex = new(@"^Set(TextColor|BorderColor|BackgroundColor)\s+(\d+)\s+(\d+)\s+(\d+)(?:\s+(\d+))?$", RegexOptions.Compiled);

    /// <summary>
    /// 解析过滤器文件，返回规则列表。
    /// </summary>
    public static List<LootFilterRule> Parse(string filePath)
    {
        var rules = new List<LootFilterRule>();
        if (!File.Exists(filePath))
            return rules;

        var lines = File.ReadAllLines(filePath);
        string currentCategory = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // 注释行（分类标题）
            if (line.StartsWith("# ---"))
            {
                // 尝试提取分类名
                var match = Regex.Match(line, @"#\s*-+\s*(.+?)\s*-+");
                if (match.Success)
                    currentCategory = match.Groups[1].Value;
                continue;
            }

            // 空行跳过
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Show/Hide 块开始
            var blockMatch = BlockStartRegex.Match(line);
            if (!blockMatch.Success)
                continue;

            var rule = new LootFilterRule
            {
                IsVisible = blockMatch.Groups[1].Value == "Show",
                Comment = blockMatch.Groups[2].Value.Trim(),
                Category = currentCategory
            };

            // 读取块体（直到下一个 Show/Hide 或空行后非缩进行）
            i++;
            while (i < lines.Length)
            {
                var bodyLine = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(bodyLine))
                    break;
                if (bodyLine.StartsWith("Show ") || bodyLine.StartsWith("Hide ") || bodyLine.StartsWith("Show#") || bodyLine.StartsWith("Hide#"))
                    break;
                if (BlockStartRegex.IsMatch(bodyLine))
                    break;

                ParseBodyLine(bodyLine, rule);
                i++;
            }

            // 从 BaseType 提取可读文本
            if (!string.IsNullOrEmpty(rule.BaseTypeCondition))
                rule.BaseTypeText = rule.BaseTypeCondition.Replace("\"", "").Trim();

            rules.Add(rule);
        }

        return rules;
    }

    private static void ParseBodyLine(string line, LootFilterRule rule)
    {
        if (line.StartsWith("Class "))
        {
            rule.ClassCondition = line.Substring(6).Trim();
        }
        else if (line.StartsWith("BaseType "))
        {
            rule.BaseTypeCondition = line.Substring(9).Trim();
        }
        else if (line.StartsWith("CustomAlertSound "))
        {
            var val = line.Substring(17).Trim();
            // 提取引号内的路径，并统一为正斜杠，避免 Windows 反斜杠与列表项匹配失败
            var match = Regex.Match(val, @"""([^""]+)""");
            var sound = match.Success ? match.Groups[1].Value : val;
            rule.CustomAlertSound = sound.Replace('\\', '/').Trim();
        }
        else if (line.StartsWith("SetFontSize "))
        {
            if (int.TryParse(line.Substring(12).Trim(), out var fs))
                rule.FontSize = fs;
        }
        else if (line.StartsWith("SetTextColor "))
        {
            var m = ColorRegex.Match(line);
            if (m.Success)
                rule.TextColor = Color.FromRgb((byte)int.Parse(m.Groups[2].Value), (byte)int.Parse(m.Groups[3].Value), (byte)int.Parse(m.Groups[4].Value));
        }
        else if (line.StartsWith("SetBorderColor "))
        {
            var m = ColorRegex.Match(line);
            if (m.Success)
                rule.BorderColor = Color.FromRgb((byte)int.Parse(m.Groups[2].Value), (byte)int.Parse(m.Groups[3].Value), (byte)int.Parse(m.Groups[4].Value));
        }
        else if (line.StartsWith("SetBackgroundColor "))
        {
            var m = ColorRegex.Match(line);
            if (m.Success)
                rule.BackgroundColor = Color.FromRgb((byte)int.Parse(m.Groups[2].Value), (byte)int.Parse(m.Groups[3].Value), (byte)int.Parse(m.Groups[4].Value));
        }
        else if (line.StartsWith("MinimapIcon "))
        {
            rule.MinimapIcon = line.Substring(12).Trim();
            ParseMinimapIcon(rule.MinimapIcon, rule);
        }
        else if (line.StartsWith("PlayEffect "))
        {
            rule.PlayEffect = line.Substring(11).Trim();
        }
        else if (line == "DisableDropSound")
        {
            rule.DisableDropSound = true;
        }
        else if (!string.IsNullOrEmpty(line))
        {
            // 其他条件（StackSize, Rarity, ItemLevel, Quality, Corrupted, Sockets 等）
            if (string.IsNullOrEmpty(rule.RawConditions))
                rule.RawConditions = line;
            else
                rule.RawConditions += "\n" + line;
        }
    }

    /// <summary>
    /// 解析 MinimapIcon 行，拆分为尺寸/颜色/形状。
    /// </summary>
    private static void ParseMinimapIcon(string value, LootFilterRule rule)
    {
        var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            rule.HasMinimapIcon = true;
            rule.MinimapIconSize = parts[0];
            rule.MinimapIconColor = parts[1];
            rule.MinimapIconShape = parts[2];
        }
        else if (parts.Length >= 1)
        {
            rule.HasMinimapIcon = true;
            rule.MinimapIconSize = parts[0];
        }
    }

    /// <summary>
    /// 从尺寸/颜色/形状组合回 MinimapIcon 字符串。
    /// </summary>
    public static string BuildMinimapIcon(LootFilterRule rule)
    {
        if (!rule.HasMinimapIcon)
            return "";
        return $"{rule.MinimapIconSize} {rule.MinimapIconColor} {rule.MinimapIconShape}";
    }

    /// <summary>
    /// 扫描游戏过滤器目录下的所有 MP3 文件（递归）。
    /// </summary>
    public static List<string> ScanMp3Files(string directory)
    {
        var result = new List<string>();
        if (!Directory.Exists(directory))
            return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.mp3", SearchOption.AllDirectories))
            {
                // 返回相对于过滤器目录的相对路径（统一正斜杠），便于写入 .filter 文件并与列表项精确匹配
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                result.Add(relative);
            }
        }
        catch
        {
            // 忽略无权限访问的目录
        }
        return result;
    }

    /// <summary>
    /// 将规则列表保存回过滤器文件。
    /// </summary>
    public static void Save(string filePath, List<LootFilterRule> rules)
    {
        var sb = new StringBuilder();
        string? lastCategory = null;

        foreach (var rule in rules)
        {
            // 写分类标题
            if (rule.Category != lastCategory)
            {
                if (lastCategory != null)
                    sb.AppendLine();
                sb.AppendLine($"# -------------------------- {rule.Category} -------------------------- #");
                sb.AppendLine();
                lastCategory = rule.Category;
            }

            sb.AppendLine($"{(rule.IsVisible ? "Show" : "Hide")} # {rule.Comment}");

            if (!string.IsNullOrEmpty(rule.RawConditions))
                sb.AppendLine($"    {rule.RawConditions.Replace("\n", "\n    ")}");

            if (!string.IsNullOrEmpty(rule.ClassCondition))
                sb.AppendLine($"    Class {rule.ClassCondition}");

            if (!string.IsNullOrEmpty(rule.BaseTypeCondition))
                sb.AppendLine($"    BaseType {rule.BaseTypeCondition}");

            sb.AppendLine($"    SetTextColor {rule.TextColor.R} {rule.TextColor.G} {rule.TextColor.B}");
            sb.AppendLine($"    SetBorderColor {rule.BorderColor.R} {rule.BorderColor.G} {rule.BorderColor.B}");
            sb.AppendLine($"    SetBackgroundColor {rule.BackgroundColor.R} {rule.BackgroundColor.G} {rule.BackgroundColor.B} 255");
            sb.AppendLine($"    SetFontSize {rule.FontSize}");

            var minimapIcon = BuildMinimapIcon(rule);
            if (!string.IsNullOrWhiteSpace(minimapIcon))
                sb.AppendLine($"    MinimapIcon {minimapIcon}");

            if (!string.IsNullOrEmpty(rule.PlayEffect))
                sb.AppendLine($"    PlayEffect {rule.PlayEffect}");

            if (!string.IsNullOrEmpty(rule.CustomAlertSound))
                sb.AppendLine($"    CustomAlertSound \"{rule.CustomAlertSound}\" 300");

            if (rule.DisableDropSound)
                sb.AppendLine("    DisableDropSound");

            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 获取默认（程序内置）过滤器文件路径。
    /// </summary>
    public static string GetDefaultFilterPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "filter", "默认过滤器.filter");
    }

    /// <summary>
    /// 获取用户自定义过滤器目录（AppData）。
    /// </summary>
    public static string GetUserFiltersDirectory()
    {
        return AppDataPath.Filters;
    }

    /// <summary>
    /// 扫描程序内置目录和用户目录下所有 .filter 文件。
    /// </summary>
    public static List<FilterFileInfo> ScanAvailableFilters()
    {
        var result = new List<FilterFileInfo>();
        var builtInDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "filter");
        var userDir = GetUserFiltersDirectory();

        if (Directory.Exists(builtInDir))
        {
            foreach (var file in Directory.EnumerateFiles(builtInDir, "*.filter", SearchOption.TopDirectoryOnly))
            {
                result.Add(new FilterFileInfo
                {
                    FilePath = file,
                    DisplayName = Path.GetFileNameWithoutExtension(file),
                    SourceLabel = "程序内置",
                    IsBuiltIn = true,
                });
            }
        }

        if (Directory.Exists(userDir))
        {
            foreach (var file in Directory.EnumerateFiles(userDir, "*.filter", SearchOption.TopDirectoryOnly))
            {
                result.Add(new FilterFileInfo
                {
                    FilePath = file,
                    DisplayName = Path.GetFileNameWithoutExtension(file),
                    SourceLabel = "用户目录",
                    IsBuiltIn = false,
                });
            }
        }

        return result;
    }
}
