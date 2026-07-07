using System;
using System.IO;
using System.Linq;

namespace Poe2PriceGui.Services;

/// <summary>
/// 检测 POE2 游戏内语言设置，并映射到 bundle 虚拟路径。
/// 参考 poe2_price-main 的 Get-Poe2ConfigLanguage + Get-Poe2LanguageInfoFromCode。
/// </summary>
public static class Poe2LanguageDetector
{
    /// <summary>
    /// 从 我的文档\My Games\Path of Exile 2\poe2_production*_Config.ini
    /// 读取 [LANGUAGE] section 的 language= 值。
    /// 多个配置文件时取最近修改的一个。
    /// </summary>
    public static string? DetectLanguageCode()
    {
        var myGames = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "Path of Exile 2");
        if (!Directory.Exists(myGames)) return null;

        var configFiles = Directory.GetFiles(myGames, "poe2_production*_Config.ini")
            .OrderByDescending(File.GetLastWriteTime)
            .ToArray();
        if (configFiles.Length == 0) return null;

        foreach (var configFile in configFiles)
        {
            bool inLanguageSection = false;
            string[] lines;
            try { lines = File.ReadAllLines(configFile); }
            catch { continue; }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inLanguageSection = trimmed[1..^1]
                        .Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inLanguageSection &&
                    trimmed.StartsWith("language", StringComparison.OrdinalIgnoreCase))
                {
                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var value = trimmed[(eqIdx + 1)..].Trim();
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 把语言代码映射到 baseitemtypes.datc64 的 bundle 虚拟路径。
    /// 参考 poe2_price-main Get-Poe2LanguageInfoFromCode。
    /// 未识别或空值回退到英文路径。
    /// </summary>
    public static string GetBaseItemsPath(string? languageCode)
        => GetBaseItemsPath(languageCode, isChina: false);

    /// <summary>
    /// 把语言代码映射到 baseitemtypes.datc64 的 bundle 虚拟路径。
    /// 参考 poe2_price-main Get-Poe2LanguageInfoFromCode + Get-Poe2InstallInfo。
    /// 国服未识别时回退到简体中文，国际服未识别时回退到繁体中文。
    /// </summary>
    public static string GetBaseItemsPath(string? languageCode, bool isChina)
    {
        var code = (languageCode ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        return code switch
        {
            "en" or "en-us" or "en-gb" or "english"
                => "data/balance/baseitemtypes.datc64",
            "zh-cn" or "zh-hans" or "simplified chinese" or "simplified-chinese" or "sc"
                => "data/balance/simplified chinese/baseitemtypes.datc64",
            "zh-tw" or "zh-hant" or "traditional chinese" or "traditional-chinese" or "tc"
                => "data/balance/traditional chinese/baseitemtypes.datc64",
            "fr" or "french" => "data/balance/french/baseitemtypes.datc64",
            "es" or "spanish" => "data/balance/spanish/baseitemtypes.datc64",
            "de" or "german" => "data/balance/german/baseitemtypes.datc64",
            "pt" or "portuguese" => "data/balance/portuguese/baseitemtypes.datc64",
            "ru" or "russian" => "data/balance/russian/baseitemtypes.datc64",
            "th" or "thai" => "data/balance/thai/baseitemtypes.datc64",
            "ja" or "japanese" => "data/balance/japanese/baseitemtypes.datc64",
            "ko" or "korean" => "data/balance/korean/baseitemtypes.datc64",
            // 未识别或空值：国服回退到简体中文，国际服回退到繁体中文。
            _ => isChina
                ? "data/balance/simplified chinese/baseitemtypes.datc64"
                : "data/balance/traditional chinese/baseitemtypes.datc64",
        };
    }

    /// <summary>
    /// 获取默认语言代码。国服默认 zh-CN，国际服默认 zh-TW。
    /// 参考 poe2_price-main: $DefaultLanguageCode = if ($IsChina) { "zh-CN" } else { "zh-TW" }。
    /// </summary>
    public static string GetDefaultLanguageCode(bool isChina)
        => isChina ? "zh-CN" : "zh-TW";

    /// <summary>
    /// 由 baseitemtypes 路径推导 words 路径（替换文件名）。
    /// </summary>
    public static string GetWordsPath(string baseItemsPath)
        => baseItemsPath.Replace("baseitemtypes.datc64", "words.datc64");

    /// <summary>
    /// 由 baseitemtypes 路径推导 endgamemaps 路径（替换文件名）。
    /// </summary>
    public static string GetEndgameMapsPath(string baseItemsPath)
        => baseItemsPath.Replace("baseitemtypes.datc64", "endgamemaps.datc64");
}
