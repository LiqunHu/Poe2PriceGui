using System.IO;
using System.Text.Json;

namespace Poe2PriceGui.Services;

/// <summary>
/// 应用设置持久化服务。
/// </summary>
public class SettingsService
{
    private readonly string _settingsFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public SettingsService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
    }

    public AppSettings Load()
    {
        var settings = new AppSettings();

        if (File.Exists(_settingsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "读取设置失败");
            }
        }

        // 若未配置目标赛季，使用默认值。
        if (string.IsNullOrWhiteSpace(settings.PriceCheckerLeague))
        {
            settings.PriceCheckerLeague = "奥杜尔秘符";
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "保存设置失败");
        }
    }
}

/// <summary>
/// 应用设置数据。
/// </summary>
public class AppSettings
{
    /// <summary>POE2 游戏根目录，例如 C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2</summary>
    public string GameDirectory { get; set; } = "";

    /// <summary>上次成功刷新价格的时间（UTC）。</summary>
    public DateTime? LastRefreshTime { get; set; }

    /// <summary>查价器是否启用。</summary>
    public bool PriceCheckerEnabled { get; set; }

    /// <summary>查价器全局热键，例如 "Ctrl+D"。</summary>
    public string PriceCheckerHotkey { get; set; } = "Ctrl+D";

    /// <summary>查价器 POESESSID Cookie。</summary>
    public string PriceCheckerPoeSessionId { get; set; } = "";

    /// <summary>查价器目标赛季，例如 "奥杜尔秘符"。</summary>
    public string PriceCheckerLeague { get; set; } = "奥杜尔秘符";

    /// <summary>查价器解析语言索引（Strings.Culture 数组索引：0=en-US, 9=zh-CN, 10=ja-JP, ...），默认中文。</summary>
    public int PriceCheckerLanguage { get; set; } = 9;

    /// <summary>通货价格查询 Token，为空时使用公共 summary 接口，非空时使用 summary_validate 接口。</summary>
    public string CurrencyPriceToken { get; set; } = "789486ce3baf2c4a7e18f4ba0b9aa4ab8edb9da64ca92bca10ca74c094cd8f8d";

    /// <summary>国服模式下，是否启用"没有的道具国际服兜底"：用国际服 poe2scout 数据补充国服 poecurrency 未覆盖的物品。默认关闭。</summary>
    public bool IntlFallbackEnabled { get; set; } = false;

    /// <summary>泥人补丁：用户勾选的补丁名称列表（持久化跨启动）。默认 performance 预设的 6 个补丁。</summary>
    public List<string> SmootherSelectedPatches { get; set; } = new()
    {
        "fog", "rain", "clouds", "env-particles", "particles", "effects",
    };

    /// <summary>泥人补丁：相机 zoom 倍率（1.2 ~ 2.4），默认 2.4。</summary>
    public double SmootherCameraZoom { get; set; } = 2.4;
}
