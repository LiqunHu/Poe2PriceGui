using System;
using System.IO;
using System.Linq;

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

    /// <summary>技能特效修改补丁目录：%LOCALAPPDATA%\Poe2PriceGui\data\patcheffect\。</summary>
    public static string SkillPatchEffect => Path.Combine(Data, "patcheffect");

    /// <summary>技能特效还原补丁目录：%LOCALAPPDATA%\Poe2PriceGui\data\orginpatcheffect\。</summary>
    public static string SkillRestoreEffect => Path.Combine(Data, "orginpatcheffect");

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
    /// 旧版泥人补丁备份位置：%LOCALAPPDATA%\Poe2PriceGui\smoother.bak。
    /// 历史遗留：早期 SmootherBackupStore 构造函数硬编码了这个目录
    /// （与 AppDataPath.Root = ...\Poe2PriceGuiData 不同），导致备份落在
    /// 一个独立目录里、用户很难找到。EnsureDirectories 启动时会把这里
    /// 的旧备份迁移到 SmootherBackup，迁移后删除旧目录。
    /// </summary>
    private static string LegacySmootherBackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Poe2PriceGui");

    private static string LegacySmootherBackup => Path.Combine(LegacySmootherBackupDir, "smoother.bak");

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
        Directory.CreateDirectory(SkillPatchEffect);
        Directory.CreateDirectory(SkillRestoreEffect);

        // 迁移历史遗留的泥人补丁备份：早期版本把 smoother.bak 写到了
        // %LOCALAPPDATA%\Poe2PriceGui\（缺少 Data 后缀），和程序其他用户数据
        // 所在的 ...\Poe2PriceGuiData\ 不在一起，用户极难找到。把它搬到
        // AppDataPath.SmootherBackup，与其它数据统一存放。
        // MigrateSmootherBackup();
    }

    /// <summary>
    /// 把旧位置（%LOCALAPPDATA%\Poe2PriceGui\smoother.bak）的泥人补丁备份
    /// 迁移到新位置（AppDataPath.SmootherBackup）。仅当旧文件存在且新位置
    /// 尚无备份时迁移，迁移后删除空的旧目录。任何异常都静默忽略，
    /// 不影响程序启动。
    /// </summary>
    private static void MigrateSmootherBackup()
    {
        try
        {
            if (!File.Exists(LegacySmootherBackup) || File.Exists(SmootherBackup))
            {
                return;
            }
            File.Copy(LegacySmootherBackup, SmootherBackup, overwrite: false);
            File.Delete(LegacySmootherBackup);

            // 旧目录若已空（除 smoother.bak 外没有其他文件）则删除，
            // 避免遗留空目录继续干扰用户；若仍有其它文件则保留。
            if (Directory.Exists(LegacySmootherBackupDir)
                && !Directory.EnumerateFileSystemEntries(LegacySmootherBackupDir).Any())
            {
                Directory.Delete(LegacySmootherBackupDir);
            }
        }
        catch
        {
            // 迁移失败不阻塞启动：最坏情况是旧备份留在旧位置，
            // 用户在新位置看不到，但 SmootherBackupStore 仍可正常新建备份。
        }
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
