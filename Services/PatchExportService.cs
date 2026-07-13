using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 将当前价格数据导出为补丁流程可用的 CSV/JSON 文件。
/// </summary>
public class PatchExportService
{
    private readonly string _outputDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public PatchExportService()
    {
        // 输出目录存到 %LOCALAPPDATA%\Poe2PriceGui\output\，避免 Velopack 升级时补丁包/备份丢失。
        _outputDirectory = AppDataPath.Output;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }

    public string OutputDirectory => _outputDirectory;
    public string PricesCsvPath => Path.Combine(_outputDirectory, "prices.csv");
    public string EditedPricesJsonPath => Path.Combine(_outputDirectory, "edited_prices.json");

    /// <summary>
    /// 导出所有当前显示的价格为 prices.csv，供 poe2_name_price_patch.py 使用。
    /// 价格文本直接复用 <see cref="PoecurrencyItem.DisplayPrice"/>，确保补丁与查询界面显示完全一致，
    /// 不再单独计算 globalDivineRatio fallback（曾导致 UI 显示 e、补丁生成 d 的不一致）。
    /// </summary>
    public async Task<int> ExportPricesCsvAsync(IEnumerable<PoecurrencyItem> prices, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var priceList = prices.ToList();

        var rows = priceList
            .Where(p => p.PriceExalted >= 1)
            .Select(p => new CsvRow
            {
                MetadataPath = "",
                Name = p.ItemName,
                Price = p.DisplayPrice,
                NewName = "",
            })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("metadata_path,name,price,new_name");
        foreach (var row in rows)
        {
            sb.AppendLine($"{Escape(row.MetadataPath)},{Escape(row.Name)},{Escape(row.Price)},{Escape(row.NewName)}");
        }

        await File.WriteAllTextAsync(PricesCsvPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        AppLogger.Instance.Info($"导出 prices.csv：{rows.Count} 条，路径：{PricesCsvPath}");
        return rows.Count;
    }

    /// <summary>
    /// 导出用户手动编辑过的价格为 edited_prices.json，便于核对与回滚。
    /// </summary>
    public async Task<int> ExportEditedPricesJsonAsync(IEnumerable<PoecurrencyItem> prices, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var edited = prices
            .Where(p => p.IsEdited)
            .Select(p => new
            {
                p.CategoryLabel,
                p.ItemName,
                p.PriceExalted,
                p.CurrencyUnit,
                p.HasError,
                p.ErrorInfo,
            })
            .ToList();

        await File.WriteAllTextAsync(
            EditedPricesJsonPath,
            JsonSerializer.Serialize(edited, _jsonOptions),
            cancellationToken);

        AppLogger.Instance.Info($"导出 edited_prices.json：{edited.Count} 条，路径：{EditedPricesJsonPath}");
        return edited.Count;
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private sealed class CsvRow
    {
        public string MetadataPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string Price { get; set; } = "";
        public string NewName { get; set; } = "";
    }
}
