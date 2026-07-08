using System;
using System.IO;

namespace Poe2PriceGui.Services;

/// <summary>
/// 集中管理用户数据目录。
///
/// 背景：早期版本把所有运行时数据（cache/data/logs/output/settings.json）放在 AppContext.BaseDirectory，
/// 这导致 Velopack 升级时旧版本的目录会被整体替换，造成以下数据全部丢失：
/// - 用户配置（settings.json）
/// - 缓存的图标（cache/）
/// - 价格快照（data/prices.json）
/// - 操作日志（logs/）
/// - 已生成的补丁包（output/）
///
/// 修复策略：所有运行时写入文件统一存到 %LOCALAPPDATA%\Poe2PriceGui\，
/// 首次启动时自动从旧位置（AppContext.BaseDirectory）迁移一次。
///
/// 仍保留在程序目录的"只读"资源：
/// - data\translations\*.json     开发者预构建的翻译表（随 Release 打包）
/// - data\Lang\                   Xiletrade 多语言数据
/// - tools\, scripts\             补丁脚本与二进制工具
/// - app.ico, *.dll               程序自身资源
/// </summary>
public static class AppDataPath
{
    /// <summary>
    /// 用户数据根目录：%LOCALAPPDATA%\Poe2PriceGui\。
    /// </summary>
    public static string Root { get; }

    /// <summary>图标缓存：%LOCALAPPDATA%\Poe2PriceGui\cache\。</summary>
    public static string Cache { get; }

    /// <summary>运行时数据：%LOCALAPPDATA%\Poe2PriceGui\data\（含 prices.json 等）。</summary>
    public static string Data { get; }

    /// <summary>价格快照完整路径。</summary>
    public static string PricesJson => Path.Combine(Data, "prices.json");

    /// <summary>日志目录：%LOCALAPPDATA%\Poe2PriceGui\logs\。</summary>
    public static string Logs { get; }

    /// <summary>补丁输出/备份目录：%LOCALAPPDATA%\Poe2PriceGui\output\。</summary>
    public static string Output { get; }

    /// <summary>用户配置：%LOCALAPPDATA%\Poe2PriceGui\settings.json。</summary>
    public static string SettingsFile { get; }

    /// <summary>运行时生成的翻译表目录（与开发者预构建的 data\translations\ 区分）。</summary>
    public static string TranslationsRuntime { get; }

    /// <summary>泥人补丁备份（沿用 SmootherBackupStore 的位置）。</summary>
    public static string SmootherBackup => Path.Combine(Root, "smoother.bak");

    /// <summary>
    /// 泥人补丁 GGPK 模式备份 zip（与 Bundles2 模式的 smoother.bak 区分）。
    /// SmootherGgpkBackupStore.Backup 从 Content.ggpk 抽出 _.index.bin 打包产生。
    /// </summary>
    public static string SmootherGgpkBackup => Path.Combine(Root, "smoother_ggpk.zip");

    static AppDataPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Root = Path.Combine(localAppData, "Poe2PriceGuiData");
        Cache = Path.Combine(Root, "cache");
        Data = Path.Combine(Root, "data");
        Logs = Path.Combine(Root, "logs");
        Output = Path.Combine(Root, "output");
        SettingsFile = Path.Combine(Root, "settings.json");
        TranslationsRuntime = Path.Combine(Data, "translations");
    }

    /// <summary>
    /// 确保所有用户数据子目录存在。
    /// 应在程序启动早期（App.OnStartup）调用一次。
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(TranslationsRuntime);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Output);
    }

    private static void MigrateFile(string srcPath, string destPath)
    {
        if (!File.Exists(srcPath) || File.Exists(destPath))
        {
            return;
        }
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.Copy(srcPath, destPath, overwrite: false);
    }

    private static void CopyDirectoryMissing(string srcDir, string destDir)
    {
        if (!Directory.Exists(srcDir))
        {
            return;
        }
        Directory.CreateDirectory(destDir);

        foreach (var srcFile in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, srcFile);
            var destFile = Path.Combine(destDir, rel);
            if (File.Exists(destFile))
            {
                continue;
            }
            var destFileDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destFileDir))
            {
                Directory.CreateDirectory(destFileDir);
            }
            try
            {
                File.Copy(srcFile, destFile, overwrite: false);
            }
            catch
            {
                // 单文件复制失败不影响整体。
            }
        }
    }
}
