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
            // 提取引号内的路径
            var match = Regex.Match(val, @"""([^""]+)""");
            rule.CustomAlertSound = match.Success ? match.Groups[1].Value : val;
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

            if (!string.IsNullOrEmpty(rule.MinimapIcon))
                sb.AppendLine($"    MinimapIcon {rule.MinimapIcon}");

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
    /// 获取默认过滤器文件路径。
    /// </summary>
    public static string GetDefaultFilterPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "filter", "默认过滤器.filter");
    }
}
