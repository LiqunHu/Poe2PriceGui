using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Poe2PriceGui.Services;

/// <summary>
/// 国际服物品名翻译器：从游戏 datc64 双文件（英文+目标语言）提取物品名映射，
/// 在 UI 显示时把英文名替换为目标语言名。
/// 参考 poe2_price-main 的 detect_base_item_layout / scan_base_items / load_base_item_pairs。
/// </summary>
public class ItemNameTranslator
{
    /// <summary>display_name 字段在 datc64 行中的 uint32 索引（偏移 = index * 4）。</summary>
    private const int DisplayNameFieldIndex = 8;

    /// <summary>UTF-16LE 编码的 "Metadata/Items/" 标记，用于定位字符串表起点。</summary>
    private static readonly byte[] MetadataMarker =
        Encoding.Unicode.GetBytes("Metadata/Items/");

    private readonly Dictionary<string, string> _translations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cacheDirectory;

    public ItemNameTranslator(string? cacheDirectory = null)
    {
        // 翻译表加载顺序：
        // 1) 程序内置 data\translations\translations_{lang}.json（开发者预构建，随 Release 打包）
        // 2) 用户运行时生成的 %LOCALAPPDATA%\Poe2PriceGui\data\translations\translations_{lang}.json
        //    （例如运行"生成繁体翻译表"工具时写入，避免污染程序目录）
        // 3) 指定 cacheDirectory 参数（测试用）
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "data", "translations");
        var runtimeDir = AppDataPath.TranslationsRuntime;
        // 优先选已有翻译表的目录，避免覆盖用户运行时生成的版本
        if (Directory.Exists(runtimeDir))
        {
            _cacheDirectory = runtimeDir;
        }
        else if (Directory.Exists(bundledDir))
        {
            _cacheDirectory = bundledDir;
        }
        else
        {
            _cacheDirectory = cacheDirectory ?? runtimeDir;
        }
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>是否已有翻译数据。</summary>
    public bool HasTranslations => _translations.Count > 0;

    /// <summary>翻译条目数。</summary>
    public int Count => _translations.Count;

    /// <summary>返回翻译表快照（用于序列化保存）。</summary>
    public IReadOnlyDictionary<string, string> GetTranslationsSnapshot() => _translations;

    /// <summary>
    /// 翻译英文物品名。无翻译表或未命中时返回原文。
    /// </summary>
    public string Translate(string englishName)
    {
        if (string.IsNullOrEmpty(englishName) || _translations.Count == 0)
            return englishName;
        return _translations.TryGetValue(englishName, out var localized) ? localized : englishName;
    }

    /// <summary>
    /// 从两份 datc64 文件（英文+目标语言）构建翻译映射。
    /// 参考 poe2_price-main load_base_item_pairs：用 metadata_path 作为 join key。
    /// </summary>
    public void BuildFromDatc64(string enDatc64Path, string tcDatc64Path)
    {
        var enBytes = File.ReadAllBytes(enDatc64Path);
        var tcBytes = File.ReadAllBytes(tcDatc64Path);
        var en = ScanBaseItems(enBytes);
        var tc = ScanBaseItems(tcBytes);

        _translations.Clear();
        foreach (var (metadataPath, enName) in en)
        {
            if (tc.TryGetValue(metadataPath, out var tcName) && !string.IsNullOrEmpty(tcName))
            {
                _translations[enName] = tcName;
            }
        }
    }

    /// <summary>
    /// 尝试从已提取的 datc64 文件构建翻译表。
    /// 查找 output/extracted_ggpk/ 下的英文版和目标语言版 baseitemtypes.datc64。
    /// 文件不存在时返回 false，不抛异常。
    /// </summary>
    public bool TryBuildFromExtractedFiles(string outputDirectory, string languageCode)
    {
        var enPath = FindDatc64Path(outputDirectory, "data/balance/baseitemtypes.datc64");
        var tcPath = FindDatc64Path(outputDirectory,
            Poe2LanguageDetector.GetBaseItemsPath(languageCode));

        if (enPath == null || tcPath == null)
        {
            AppLogger.Instance.Info(
                $"翻译表构建跳过：datc64 文件不存在（en={enPath ?? "null"}, tc={tcPath ?? "null"}）");
            return false;
        }

        try
        {
            BuildFromDatc64(enPath, tcPath);
            AppLogger.Instance.Info($"翻译表构建成功：{Count} 条映射（{languageCode}）");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"翻译表构建失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 查找已提取的 datc64 文件。兼容 GGPK 模式（extracted_ggpk/）和 Bundles2 模式（extracted/）的目录结构。
    /// GGPKExtractor 实际输出为扁平化文件名：把虚拟路径中的 '/' 替换为 '_'，放在 output/data/ 下。
    /// 参考 poe2_price-main: $LanguageFileSlug = $LanguagePath -replace '/','_'; 输出到 $LatestDir/data/$LanguageFileSlug。
    /// </summary>
    private static string? FindDatc64Path(string outputDirectory, string virtualPath)
    {
        // virtualPath 如 "data/balance/simplified chinese/baseitemtypes.datc64"
        var relativePath = virtualPath.Replace('/', Path.DirectorySeparatorChar);
        // 扁平化：把 / 替换为 _，得到 "data_balance_simplified chinese_baseitemtypes.datc64"
        var flattenedName = virtualPath.Replace('/', '_');

        var candidates = new[]
        {
            // GGPK 模式（GGPKExtractor 输出：把 / 替换为 _，放在 data/ 下）— 首选
            Path.Combine(outputDirectory, "extracted_ggpk", "data", flattenedName),
            // GGPK 模式旧格式（兼容旧缓存，假设保留内部路径结构）
            Path.Combine(outputDirectory, "extracted_ggpk", relativePath),
            // Bundles2 模式（ExtractDatc64ForTranslationAsync 提取后移动到保留路径结构的位置）
            Path.Combine(outputDirectory, "extracted", relativePath),
            // Bundles2 模式旧格式（BundleExtractor 直接输出：把 / 替换为 _）
            Path.Combine(outputDirectory, "dat_files_latest", "data",
                relativePath.Replace(Path.DirectorySeparatorChar, '_')),
            // 备用路径
            Path.Combine(outputDirectory, relativePath),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 加载缓存的翻译表。缓存文件为 JSON 格式：{ "Exalted Orb": "崇高石", ... }。
    ///
    /// 加载顺序（找到第一个非空即停）：
    /// 1) _cacheDirectory（运行时优先，开发者预构建 fallback）
    /// 2) 程序目录 data\translations\（开发者预构建的兜底）
    /// </summary>
    public async Task<bool> LoadCacheAsync(string languageCode)
    {
        var candidates = new List<string>
        {
            GetCachePath(languageCode),
            Path.Combine(AppContext.BaseDirectory, "data", "translations", $"translations_{languageCode}.json"),
        };

        foreach (var cachePath in candidates)
        {
            if (!File.Exists(cachePath)) continue;
            try
            {
                await using var stream = File.OpenRead(cachePath);
                var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream);
                if (dict == null || dict.Count == 0) continue;

                _translations.Clear();
                foreach (var (k, v) in dict)
                {
                    _translations[k] = v;
                }
                AppLogger.Instance.Info($"翻译表缓存加载成功：{Count} 条映射（{languageCode}，源：{cachePath}）");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warn($"翻译表缓存加载失败：{cachePath} - {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>保存翻译表到缓存。</summary>
    public async Task SaveCacheAsync(string languageCode)
    {
        var cachePath = GetCachePath(languageCode);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await using var stream = File.Create(cachePath);
            await JsonSerializer.SerializeAsync(stream, _translations,
                new JsonSerializerOptions { WriteIndented = false });
            AppLogger.Instance.Info($"翻译表缓存保存成功：{Count} 条映射 → {cachePath}");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"翻译表缓存保存失败：{ex.Message}");
        }
    }

    private string GetCachePath(string languageCode)
        => Path.Combine(_cacheDirectory, $"translations_{languageCode}.json");

    /// <summary>
    /// 解析 datc64 二进制数据，返回 metadata_path → display_name 字典。
    /// 参考 poe2_name_price_patch.py 的 detect_base_item_layout + scan_base_items。
    /// </summary>
    private static Dictionary<string, string> ScanBaseItems(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (data.Length < 16) return result;

        // 1. detect_base_item_layout
        int rowCount = BitConverter.ToInt32(data, 0);
        int firstNameRel = BitConverter.ToInt32(data, 4);

        int firstMetadata = IndexOfBytes(data, MetadataMarker);
        if (firstMetadata < 0) return result;

        int stringBase = firstMetadata - firstNameRel;
        if (stringBase <= 4 || stringBase >= data.Length) return result;

        int rowBytes = stringBase - 4;
        if (rowCount <= 0 || rowBytes % rowCount != 0) return result;

        int rowSize = rowBytes / rowCount;
        if (rowSize <= DisplayNameFieldIndex * 4 || rowSize % 4 != 0) return result;

        // 2. scan_base_items
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            int rowStart = 4 + rowIndex * rowSize;
            if (rowStart + rowSize > data.Length) break;

            int metadataOffset = BitConverter.ToInt32(data, rowStart);
            int nameOffset = BitConverter.ToInt32(data, rowStart + DisplayNameFieldIndex * 4);

            string metadataPath = ReadUtf16LeZ(data, stringBase + metadataOffset);
            string name = ReadUtf16LeZ(data, stringBase + nameOffset);

            if (metadataPath.StartsWith("Metadata/Items/", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(name) &&
                !name.StartsWith("Metadata/", StringComparison.Ordinal) &&
                name.Length <= 160)
            {
                result[metadataPath] = name;
            }
        }

        return result;
    }

    /// <summary>
    /// 读取 UTF-16LE 零终止字符串。参考 poe2_name_price_patch.py 的 read_utf16le_z。
    /// </summary>
    private static string ReadUtf16LeZ(byte[] data, int start)
    {
        if (start < 0 || start >= data.Length - 1) return string.Empty;

        var sb = new StringBuilder(32);
        int pos = start;
        while (pos + 1 < data.Length)
        {
            char c = (char)(data[pos] | (data[pos + 1] << 8));
            if (c == 0) break;
            sb.Append(c);
            pos += 2;
        }
        return sb.ToString();
    }

    /// <summary>在字节数组中查找子字节序列。</summary>
    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}
