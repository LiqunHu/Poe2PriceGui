using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime;
using System.Text.RegularExpressions;
using LibBundle3;
using LibBundledGGPK3;
using BundleIndex = LibBundle3.Index;

namespace Poe2PriceGui.Services;

/// <summary>
/// 调用 poe2_name_price_patch.py 与 Bundles2/GGPK 工具生成并安装补丁。
/// </summary>
public class PatchInstaller
{
    private readonly PatchExportService _exportService;
    private readonly PatchSandboxService _sandboxService;

    public PatchInstaller(PatchExportService exportService)
    {
        _exportService = exportService;
        _sandboxService = new PatchSandboxService();
    }

    /// <summary>
    /// 补丁操作会产生大量临时大对象（LOH），默认 GC 不会立即回收，
    /// 导致任务管理器中看到的内存占用持续增长。操作完成后主动触发一次
    /// Full GC 并压缩大对象堆，把已释放的内存还给系统。
    /// </summary>
    internal static void CollectAfterPatch()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch
        {
            // 回收失败不应影响补丁安装结果。
        }
    }

    /// <summary>
    /// 获取服务器特定的 Bundles2 还原包名称。
    /// 参考 poe2_price-main: 国服用"国服还原包.zip"，国际服用"国际服还原补丁.zip"。
    /// </summary>
    private static string GetBundles2RestoreZipName(bool isChina)
        => isChina ? "国服还原包.zip" : "国际服还原补丁.zip";

    /// <summary>
    /// 获取服务器特定的 GGPK 还原包名称。
    /// GGPK 模式仅用于国际服，统一使用"国际服还原补丁.zip"。
    /// </summary>
    private static string GetGgpkRestoreZipName()
        => "国际服还原补丁.zip";

    /// <summary>
    /// 查找已存在的还原包路径，兼容新旧命名。
    /// 新命名：国服还原包.zip / 国际服还原补丁.zip
    /// 旧命名：bundles2_backup.zip / ggpk_restore.zip
    /// </summary>
    private static string? FindExistingRestoreZip(string backupDir, bool isChina, bool isGgpk)
    {
        var newName = isGgpk ? GetGgpkRestoreZipName() : GetBundles2RestoreZipName(isChina);
        var oldName = isGgpk ? "ggpk_restore.zip" : "bundles2_backup.zip";

        var newPath = Path.Combine(backupDir, newName);
        if (File.Exists(newPath)) return newPath;

        var oldPath = Path.Combine(backupDir, oldName);
        if (File.Exists(oldPath)) return oldPath;

        return null;
    }

    /// <summary>
    /// 仅生成 zip 补丁包到 output 目录，不修改游戏文件。
    /// </summary>
    public async Task<InstallResult> ExportPatchZipAsync(
        IEnumerable<Models.PoecurrencyItem> prices,
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        return await BuildAndMaybeInstallAsync(prices, gameDirectory, install: false, null, cancellationToken);
    }

    /// <summary>
    /// 生成补丁并安装到游戏目录。
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        IEnumerable<Models.PoecurrencyItem> prices,
        string gameDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await BuildAndMaybeInstallAsync(prices, gameDirectory, install: true, progress, cancellationToken);
    }

    /// <summary>
    /// 还原原始备份文件（ggpk_restore.zip / bundles2_backup.zip / .original）。
    /// </summary>
    public async Task<InstallResult> RestoreLatestBackupAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        var result = new InstallResult();

        var modeInfo = GameModeDetector.Detect(gameDirectory);
        if (!modeInfo.IsValid)
        {
            result.ErrorMessage = modeInfo.ErrorMessage;
            return result;
        }

        var backupDir = Path.Combine(_exportService.OutputDirectory, "backup");

        if (modeInfo.Mode == GameMode.GGPK)
        {
            var targetFile = Path.Combine(gameDirectory, "Content.ggpk");
            var restoreZip = FindExistingRestoreZip(backupDir, isChina: false, isGgpk: true);

            // 优先从还原 zip 还原（仅几 MB），兼容旧版 Content.ggpk.original（100GB 完整复制）。
            if (restoreZip != null)
            {
                // GGPK 还原已改为内置 BundledGGPK + Index.Replace，不再依赖外部工具。
                var restoreResult = await RestoreGgpkFromZipAsync(gameDirectory, restoreZip, modeInfo, cancellationToken);
                if (!restoreResult.Success)
                {
                    return restoreResult;
                }
                AppLogger.Instance.Info($"从还原包还原：{restoreZip} -> {targetFile}");
                return restoreResult;
            }

            var ggpkOldBackup = Path.Combine(backupDir, "Content.ggpk.original");
            if (!File.Exists(ggpkOldBackup))
            {
                result.ErrorMessage = $"未找到 GGPK 还原包或原始备份文件：{restoreZip}";
                return result;
            }

            try
            {
                File.Copy(ggpkOldBackup, targetFile, overwrite: true);
                AppLogger.Instance.Info($"还原原始备份（旧格式）：{ggpkOldBackup} -> {targetFile}");
                result.Success = true;
                result.InstalledPath = targetFile;
                result.GameMode = modeInfo.DisplayName;
                result.BackupPath = ggpkOldBackup;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "还原备份失败");
                result.ErrorMessage = $"还原备份失败：{ex.Message}";
            }
            return result;
        }

        // Bundles2 模式：优先从 ZIP 还原，兼容旧版 .original 文件
        var zipBackup = FindExistingRestoreZip(backupDir, modeInfo.IsChina, isGgpk: false);
        if (zipBackup != null)
        {
            try
            {
                var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
                using var archive = ZipFile.OpenRead(zipBackup);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var destPath = Path.Combine(bundles2Dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                    AppLogger.Instance.Info($"还原备份文件：{entry.FullName} -> {destPath}");
                }
                result.Success = true;
                result.InstalledPath = Path.Combine(bundles2Dir, "_.index.bin");
                result.GameMode = modeInfo.DisplayName;
                result.BackupPath = zipBackup;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "从 ZIP 还原备份失败");
                result.ErrorMessage = $"从 ZIP 还原备份失败：{ex.Message}";
            }
            return result;
        }

        // 兼容旧版：仅还原 _.index.bin.original
        var oldBackup = Path.Combine(backupDir, "_.index.bin.original");
        var oldTarget = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");
        if (File.Exists(oldBackup))
        {
            try
            {
                File.Copy(oldBackup, oldTarget, overwrite: true);
                AppLogger.Instance.Info($"还原原始备份（旧格式）：{oldBackup} -> {oldTarget}");
                result.Success = true;
                result.InstalledPath = oldTarget;
                result.GameMode = modeInfo.DisplayName;
                result.BackupPath = oldBackup;
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "还原备份失败");
                result.ErrorMessage = $"还原备份失败：{ex.Message}";
            }
            return result;
        }

        result.ErrorMessage = $"未找到备份文件：{zipBackup} 或 {oldBackup}";
        return result;
    }

    /// <summary>
    /// 检测 Bundles2 模式下是否已应用过价格补丁。
    /// 判定依据：Bundles2/LibGGPK3/ 目录存在且包含文件。
    /// LibGGPK3/ 在首次应用价格补丁时由 Index.Replace 创建，存放增量 bundle。
    /// 存在该目录说明 _.index.bin 中已有 LibGGPK3/ 前缀记录，可走增量更新流程。
    /// </summary>
    private static bool IsPricePatchAlreadyAppliedBundles2(string gameDirectory)
    {
        var libDir = Path.Combine(gameDirectory, "Bundles2", "LibGGPK3");
        if (!Directory.Exists(libDir)) return false;
        try
        {
            return Directory.GetFiles(libDir, "*", SearchOption.AllDirectories).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<InstallResult> BuildAndMaybeInstallAsync(
        IEnumerable<Models.PoecurrencyItem> prices,
        string gameDirectory,
        bool install,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new InstallResult();

        // 1. 导出 CSV。
        progress?.Report("1/6 正在导出价格数据...");
        var exportedCount = await _exportService.ExportPricesCsvAsync(prices, cancellationToken);
        result.ExportedCount = exportedCount;

        // 2. 检测游戏版本。
        progress?.Report("2/6 正在检测游戏版本...");
        var modeInfo = GameModeDetector.Detect(gameDirectory);
        if (!modeInfo.IsValid)
        {
            result.ErrorMessage = modeInfo.ErrorMessage;
            return result;
        }

        result.GameMode = modeInfo.DisplayName;

        // 3. 工具已内置（LibBundle3/LibBundledGGPK3），无需校验外部工具。
        progress?.Report("3/6 正在校验环境...");

        // 4. 检测增量更新模式（仅 Bundles2 模式）。
        //    若 LibGGPK3/ 目录存在且非空，说明已应用过价格补丁。此时可跳过还原步骤，
        //    直接在当前已打补丁的 datc64 上增量更新（Python 脚本通过 --keep-existing-price
        //    剥离旧价格后缀再应用新价格）。这样保留 _.index.bin 中的其他前缀记录
        //    （如 TinyPoe2Smoother/），避免与泥人补丁冲突。
        //    GGPK 模式不涉及泥人补丁冲突，仍走原还原流程。
        var backupDir = Path.Combine(_exportService.OutputDirectory, "backup");
        string targetGameFile = modeInfo.Mode == GameMode.GGPK
            ? Path.Combine(gameDirectory, "Content.ggpk")
            : Path.Combine(gameDirectory, "Bundles2", "_.index.bin");

        bool incrementalUpdate = false;
        if (modeInfo.Mode == GameMode.Bundles2 && IsPricePatchAlreadyAppliedBundles2(gameDirectory))
        {
            incrementalUpdate = true;
            progress?.Report("4/6 检测到已应用价格补丁，使用增量更新模式（跳过还原）...");
            AppLogger.Instance.Info("增量更新模式：检测到 LibGGPK3/ 目录存在，跳过还原步骤，保留 _.index.bin 现有记录");
        }

        if (modeInfo.Mode == GameMode.GGPK)
        {
            // GGPK 模式：优先从还原包还原（仅几 MB），兼容旧版 Content.ggpk.original（100GB 完整复制）。
            var ggpkRestoreZip = FindExistingRestoreZip(backupDir, isChina: false, isGgpk: true);
            if (ggpkRestoreZip != null)
            {
                progress?.Report("4/6 正在从还原包还原原始数据文件...");
                var restoreResult = await RestoreGgpkFromZipAsync(gameDirectory, ggpkRestoreZip, modeInfo, cancellationToken);
                if (!restoreResult.Success)
                {
                    result.ErrorMessage = restoreResult.ErrorMessage;
                    return result;
                }
                AppLogger.Instance.Info($"安装前从还原包还原：{ggpkRestoreZip}");
            }
            else
            {
                var ggpkOldBackup = Path.Combine(backupDir, "Content.ggpk.original");
                if (File.Exists(ggpkOldBackup))
                {
                    progress?.Report("4/6 正在还原原始数据文件（旧格式完整备份）...");
                    try
                    {
                        File.Copy(ggpkOldBackup, targetGameFile, overwrite: true);
                        AppLogger.Instance.Info($"安装前还原原始备份（旧格式）：{ggpkOldBackup} -> {targetGameFile}");
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = $"还原原始备份失败：{ex.Message}";
                        return result;
                    }
                }
            }
        }
        else if (!incrementalUpdate)
        {
            // Bundles2 模式（非增量更新）：优先从 ZIP 还原，兼容旧版 .original 文件。
            // 增量更新模式下跳过此步骤，保留 _.index.bin 中的所有前缀记录。
            var zipBackup = FindExistingRestoreZip(backupDir, modeInfo.IsChina, isGgpk: false);
            var oldBackup = Path.Combine(backupDir, "_.index.bin.original");
            if (zipBackup != null)
            {
                progress?.Report("4/6 正在还原原始数据文件...");
                try
                {
                    var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
                    using var archive = ZipFile.OpenRead(zipBackup);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var destPath = Path.Combine(bundles2Dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                    AppLogger.Instance.Info($"安装前从 ZIP 还原原始备份：{zipBackup}");
                }
                catch (Exception ex)
                {
                    result.ErrorMessage = $"还原原始备份失败：{ex.Message}";
                    return result;
                }
            }
            else if (File.Exists(oldBackup))
            {
                progress?.Report("4/6 正在还原原始数据文件...");
                try
                {
                    File.Copy(oldBackup, targetGameFile, overwrite: true);
                    AppLogger.Instance.Info($"安装前还原原始备份（旧格式）：{oldBackup} -> {targetGameFile}");
                }
                catch (Exception ex)
                {
                    result.ErrorMessage = $"还原原始备份失败：{ex.Message}";
                    return result;
                }
            }
        }

        string sourceDat;
        if (modeInfo.Mode == GameMode.Bundles2)
        {
            if (incrementalUpdate)
            {
                // 增量更新：从当前 _.index.bin 提取已打补丁的 datc64（包含旧价格后缀），
                // 后续 Python 脚本通过 --keep-existing-price 剥离旧价格后再应用新价格。
                progress?.Report("4/6 正在提取当前数据文件（增量更新）...");
            }
            else
            {
                progress?.Report("4/6 正在从 Bundles2 提取原始数据文件...");
            }
            var extracted = await ExtractFromBundles2Async(gameDirectory, modeInfo.BaseItemsPath, cancellationToken);
            if (!extracted.Success)
            {
                result.ErrorMessage = extracted.ErrorMessage;
                return result;
            }
            sourceDat = extracted.FilePath;
        }
        else
        {
            // GGPK 模式：内置 BundledGGPK 从 Content.ggpk 提取 datc64 到临时目录。
            progress?.Report("4/6 正在从 Content.ggpk 提取原始数据文件...");
            var extracted = await ExtractFromGgpkAsync(gameDirectory, modeInfo.BaseItemsPath, cancellationToken);
            if (!extracted.Success)
            {
                result.ErrorMessage = extracted.ErrorMessage;
                return result;
            }
            sourceDat = extracted.FilePath;

            // 将提取的原始 datc64 打包成还原 zip（仅几 MB），避免备份整个 Content.ggpk（可达 100GB）。
            // 还原时通过内置 BundledGGPK + Index.Replace 将这些干净条目写回 Content.ggpk。
            var ggpkRestoreZip = Path.Combine(backupDir, GetGgpkRestoreZipName());
            try
            {
                CreateGgpkRestoreZip(ggpkRestoreZip, sourceDat, modeInfo.BaseItemsPath);
                AppLogger.Instance.Info($"已创建 GGPK 还原包：{ggpkRestoreZip}");
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"创建 GGPK 还原包失败：{ex.Message}";
                return result;
            }
        }

        // 5. 生成补丁 datc64 与 zip。
        //    增量更新模式下传 keepExistingPrice=true，Python 脚本会先剥离旧价格后缀再应用新价格。
        progress?.Report("5/6 正在生成补丁文件...");
        var patchedDat = Path.Combine(_exportService.OutputDirectory, "patched_baseitemtypes.datc64");
        var zipPath = Path.Combine(_exportService.OutputDirectory, "物价补丁.zip");
        var scriptPath = ResolvePatchScriptPath();
        var buildResult = await RunPythonPatchScriptAsync(
            scriptPath,
            sourceDat,
            _exportService.PricesCsvPath,
            patchedDat,
            modeInfo.BaseItemsPath,
            zipPath,
            keepExistingPrice: incrementalUpdate,
            cancellationToken);

        if (!buildResult.Success)
        {
            result.ErrorMessage = buildResult.ErrorMessage;
            return result;
        }

        if (!File.Exists(zipPath))
        {
            result.ErrorMessage = $"补丁 zip 未生成：{zipPath}";
            return result;
        }

        if (!install)
        {
            result.Success = true;
            result.InstalledPath = zipPath;
            AppLogger.Instance.Info($"补丁包生成完成：{zipPath}");
            return result;
        }

        // 6. 备份并安装补丁。
        progress?.Report("6/6 正在备份并安装补丁...");
        var installResult = modeInfo.Mode == GameMode.GGPK
            ? await InstallToGgpkAsync(gameDirectory, zipPath, cancellationToken)
            : await InstallToBundles2Async(gameDirectory, zipPath, incrementalUpdate, modeInfo.IsChina, sourceDat, modeInfo.BaseItemsPath, cancellationToken);
        // 回填导出数量和游戏模式（安装方法创建新 InstallResult，需保留前序信息）。
        installResult.ExportedCount = exportedCount;
        installResult.GameMode = modeInfo.DisplayName;
        CollectAfterPatch();
        return installResult;
    }

    private async Task<InstallResult> InstallToGgpkAsync(
        string gameDirectory,
        string zipPath,
        CancellationToken cancellationToken)
    {
        var ggpkPath = Path.Combine(gameDirectory, "Content.ggpk");
        // GGPK 模式不备份整个 Content.ggpk（可能高达 100GB），
        // 还原包（国际服还原补丁.zip，仅含 datc64 小文件）在 BuildAndMaybeInstallAsync 提取阶段已创建。
        var result = new InstallResult
        {
            BackupPath = Path.Combine(_exportService.OutputDirectory, "backup", GetGgpkRestoreZipName())
        };

        // 直接用 BundledGGPK + LibBundle3.Index.Replace
        // 将补丁 zip 中的条目写入 GGPK 内的 bundle。saveIndex=true 确保索引立即落盘。
        AppLogger.Instance.Info($"安装 GGPK 补丁（内置）：ggpk={ggpkPath}, zip={zipPath}");
        try
        {
            int replaced = await Task.Run(() =>
            {
                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                var failedPaths = ggpk.Index.ParsePaths();
                if (failedPaths > 0)
                {
                    AppLogger.Instance.Warn($"GGPK 索引解析：{failedPaths} 个文件路径解析失败（已忽略）");
                }

                using var zip = ZipFile.OpenRead(zipPath);
                return BundleIndex.Replace(
                    ggpk.Index,
                    zip.Entries,
                    (fileRecord, path) =>
                    {
                        var bundlePath = fileRecord.BundleRecord?.Path ?? "<unknown>";
                        AppLogger.Instance.Info($"  GGPK 替换：{path} -> size={fileRecord.Size} bundle={bundlePath}");
                        return false;
                    },
                    saveIndex: true);
            }, cancellationToken);

            AppLogger.Instance.Info($"GGPK 补丁安装完成：替换/新增 {replaced} 个文件，ggpk={ggpkPath}");
            result.Success = true;
            result.InstalledPath = ggpkPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "GGPK 补丁安装失败");
            result.ErrorMessage = $"GGPK 补丁安装失败：{ex.Message}";
        }

        return result;
    }

    private async Task<InstallResult> InstallToBundles2Async(
        string gameDirectory,
        string zipPath,
        bool incrementalUpdate,
        bool isChina,
        string sourceDat,
        string baseItemsPath,
        CancellationToken cancellationToken)
    {
        var indexBin = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");
        var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
        var backupDir = Path.Combine(_exportService.OutputDirectory, "backup");
        Directory.CreateDirectory(backupDir);
        // 使用 ZIP 保存"原始"备份，包含 _.index.bin、_.index.high.bin、_.index.low.bin、.index.dbg 和 LibGGPK3/ 目录
        // 只在首次安装时创建，避免备份已打补丁的文件导致无法还原。
        // 服务器特定的命名：国服 → 国服还原包.zip，国际服 → 国际服还原补丁.zip
        var restoreZipName = GetBundles2RestoreZipName(isChina);
        var zipBackupPath = Path.Combine(backupDir, restoreZipName);
        var oldBackupPath = Path.Combine(backupDir, "_.index.bin.original");
        var result = new InstallResult
        {
            BackupPath = zipBackupPath
        };

        // 增量更新模式下跳过备份创建：此时 _.index.bin 已含 LibGGPK3/ 与 TinyPoe2Smoother/ 等记录，
        // 备份已打补丁的状态会污染原始备份导致后续无法正确还原。原始备份应在首次安装时已创建。
        // 兼容旧版 bundles2_backup.zip
        var existingBackup = FindExistingRestoreZip(backupDir, isChina, isGgpk: false);
        if (incrementalUpdate && existingBackup == null)
        {
            // 干净迁移基线（v0.4.9.3）：增量更新但无安全还原包时，
            // 在沙盒中剥离所有物价标记后打包为还原包（baseline_kind=semantic-clean-migration），
            // 而非直接打包已打补丁的脏状态。真实游戏不会被修改。
            // 参考 New-CleanPhysicalRestoreZipFromPatchedSources。
            AppLogger.Instance.Warn($"增量更新模式：{restoreZipName} 不存在，正在构建干净迁移基线...");
            try
            {
                await CreateCleanMigrationBaselineAsync(
                    gameDirectory,
                    bundles2Dir,
                    sourceDat,
                    baseItemsPath,
                    zipBackupPath,
                    isChina,
                    cancellationToken);
                existingBackup = zipBackupPath;
                AppLogger.Instance.Info($"已构建干净迁移基线：{zipBackupPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warn($"构建干净迁移基线失败（将继续安装）：{ex.Message}");
            }
        }
        else if (!incrementalUpdate)
        {
            try
            {
                // 如果旧版备份已存在（新名或旧名），跳过创建
                if (existingBackup == null)
                {
                    // 首次安装：用沙盒服务创建带 v2 manifest 的还原包
                    _sandboxService.CreateRestoreZipFromCurrentState(bundles2Dir, gameDirectory, zipBackupPath);

                    // 如果旧版 .original 存在，已包含在还原包中，删除旧文件
                    if (File.Exists(oldBackupPath))
                    {
                        AppLogger.Instance.Info($"迁移旧版备份到 ZIP：{oldBackupPath}");
                        File.Delete(oldBackupPath);
                    }
                    // 若旧版 bundles2_backup.zip 存在，迁移后删除
                    var legacyZip = Path.Combine(backupDir, "bundles2_backup.zip");
                    if (File.Exists(legacyZip) && legacyZip != zipBackupPath)
                    {
                        File.Delete(legacyZip);
                        AppLogger.Instance.Info($"已迁移旧版 bundles2_backup.zip → {restoreZipName}");
                    }
                }
                else
                {
                    AppLogger.Instance.Info($"原始备份 ZIP 已存在，跳过备份：{existingBackup}");
                    // 若旧版备份存在但名称不是新格式，迁移到新名称
                    if (existingBackup != zipBackupPath)
                    {
                        try
                        {
                            File.Move(existingBackup, zipBackupPath);
                            AppLogger.Instance.Info($"已迁移还原包：{existingBackup} → {zipBackupPath}");
                            result.BackupPath = zipBackupPath;
                        }
                        catch (Exception moveEx)
                        {
                            AppLogger.Instance.Warn($"迁移还原包失败（不影响功能）：{moveEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"备份 Bundles2 文件失败：{ex.Message}";
                return result;
            }
        }
        else
        {
            AppLogger.Instance.Info($"增量更新模式：跳过备份创建，使用现有备份：{existingBackup}");
        }

        // ===== 沙盒验证（v0.4.9.4：隔离构建、完整校验后原子发布）=====
        // 1. 写前并发指纹：记录写入前 Bundles2 状态
        Bundles2Fingerprint? writePrecondition = null;
        try
        {
            writePrecondition = _sandboxService.ComputeBundles2Fingerprint(bundles2Dir);
            AppLogger.Instance.Info($"写前指纹：{writePrecondition.InventorySha256[..12]}...（{writePrecondition.Files.Count} 个文件）");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"计算写前指纹失败（继续安装）：{ex.Message}");
        }

        // 2. 沙盒验证：在临时沙盒中应用补丁，成功前不修改真实游戏
        if (writePrecondition != null)
        {
            AppLogger.Instance.Info("沙盒验证：正在临时沙盒中验证补丁...");
            var sandboxResult = await _sandboxService.ValidatePatchInSandboxAsync(
                bundles2Dir, zipPath, cancellationToken);

            if (!sandboxResult.Success)
            {
                result.ErrorMessage = $"沙盒验证失败，已中止写入真实游戏：{sandboxResult.ErrorMessage}";
                AppLogger.Instance.Error(new InvalidOperationException(sandboxResult.ErrorMessage), "沙盒验证失败");
                return result;
            }

            // 3. 写前指纹复核：沙盒验证期间检测是否有并发修改
            if (!_sandboxService.ValidateFingerprint(writePrecondition, bundles2Dir))
            {
                result.ErrorMessage = "写前指纹复核失败：Bundles2 状态在沙盒验证期间已并发变化，已中止写入";
                AppLogger.Instance.Error(new InvalidOperationException(result.ErrorMessage), "写前并发检测失败");
                _sandboxService.CleanupSandbox(sandboxResult.SandboxBundles2Dir);
                return result;
            }

            AppLogger.Instance.Info($"沙盒验证通过，开始写入真实游戏（沙盒替换 {sandboxResult.ReplacedCount} 个文件）");
            _sandboxService.CleanupSandbox(sandboxResult.SandboxBundles2Dir);
        }

        // 4. 写入真实游戏：直接用 LibBundle3.Index.Replace 将补丁 zip 中的条目
        // 写入 _.index.bin（磁盘文件模式）。saveIndex=true 确保索引立即落盘。
        // LibBundle3 的操作是同步阻塞 IO，放到线程池避免卡死 UI。
        AppLogger.Instance.Info($"安装 Bundles2 补丁（内置）：index={indexBin}, zip={zipPath}");
        try
        {
            int replaced = await Task.Run(() =>
            {
                var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
                var factory = new DriveBundleFactory(bundles2Dir);
                try
                {
                    using var index = new BundleIndex(indexBin, false, factory);
                    var failedPaths = index.ParsePaths();
                    if (failedPaths > 0)
                    {
                        AppLogger.Instance.Warn($"Bundles2 索引解析：{failedPaths} 个文件路径解析失败（已忽略）");
                    }

                    using var zip = ZipFile.OpenRead(zipPath);
                    return BundleIndex.Replace(
                        index,
                        zip.Entries,
                        (fileRecord, path) =>
                        {
                            var bundlePath = fileRecord.BundleRecord?.Path ?? "<unknown>";
                            AppLogger.Instance.Info($"  Bundles2 替换：{path} -> size={fileRecord.Size} bundle={bundlePath}");
                            return false;
                        },
                        saveIndex: true);
                }
                finally
                {
                    (factory as IDisposable)?.Dispose();
                }
            }, cancellationToken);

            AppLogger.Instance.Info($"Bundles2 补丁安装完成：替换/新增 {replaced} 个文件，index={indexBin}");

            // 5. 写后读回校验：确认 Bundles2 状态已变化（写入生效）
            if (writePrecondition != null)
            {
                try
                {
                    var postWriteFingerprint = _sandboxService.ComputeBundles2Fingerprint(bundles2Dir);
                    if (string.Equals(
                            writePrecondition.InventorySha256,
                            postWriteFingerprint.InventorySha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // 写后指纹与写前相同，写入可能未生效
                        AppLogger.Instance.Warn("写后读回校验：指纹未变化，写入可能未生效（尝试回滚）");
                        result.ErrorMessage = "写后读回校验失败：Bundles2 状态未变化，写入可能未生效";

                        // 自动回滚：从还原包恢复
                        if (existingBackup != null && File.Exists(existingBackup))
                        {
                            AppLogger.Instance.Warn($"自动回滚：正在从还原包恢复 {existingBackup}...");
                            await RestoreBundles2FromZipAsync(existingBackup, bundles2Dir, cancellationToken);
                            AppLogger.Instance.Warn("自动回滚完成");
                        }
                        return result;
                    }

                    AppLogger.Instance.Info($"写后读回校验通过：指纹已变化（{postWriteFingerprint.Files.Count} 个文件）");
                }
                catch (Exception ex)
                {
                    // 读回校验异常不阻断安装（补丁可能已成功写入）
                    AppLogger.Instance.Warn($"写后读回校验异常（补丁可能已成功）：{ex.Message}");
                }
            }

            result.Success = true;
            result.InstalledPath = indexBin;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "Bundles2 补丁安装失败");

            // 自动回滚：写入异常时从还原包恢复
            if (existingBackup != null && File.Exists(existingBackup))
            {
                try
                {
                    AppLogger.Instance.Warn($"安装异常，自动回滚：正在从还原包恢复 {existingBackup}...");
                    await RestoreBundles2FromZipAsync(existingBackup, bundles2Dir, cancellationToken);
                    AppLogger.Instance.Warn("自动回滚完成");
                }
                catch (Exception rollbackEx)
                {
                    AppLogger.Instance.Error(rollbackEx, "自动回滚失败");
                }
            }

            result.ErrorMessage = $"Bundles2 补丁安装失败：{ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// 从还原 ZIP 恢复 Bundles2 目录：解压所有条目到 Bundles2/ 目录。
    /// 条目路径以 Bundles2/ 前缀开头，解压时剥离前缀。
    /// 跳过 manifest.json。用于写后校验失败或安装异常时的自动回滚。
    /// </summary>
    private async Task RestoreBundles2FromZipAsync(
        string restoreZip,
        string bundles2Dir,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(restoreZip);
            var bundles2Full = Path.GetFullPath(bundles2Dir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.FullName == "manifest.json") continue;
                cancellationToken.ThrowIfCancellationRequested();

                // 剥离 Bundles2/ 前缀
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                const string prefix = "Bundles2" + "\\";
                if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = relativePath.Substring(prefix.Length);
                }
                else if (relativePath.StartsWith("Bundles2/", StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = relativePath.Substring("Bundles2/".Length);
                }

                var destPath = Path.Combine(bundles2Dir, relativePath);

                // 安全检查：确保解压路径在 bundles2Dir 之下（防止路径穿越）
                var destFull = Path.GetFullPath(destPath);
                if (!destFull.StartsWith(bundles2Full, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Instance.Warn($"跳过可疑条目（路径穿越）：{entry.FullName}");
                    continue;
                }

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }, cancellationToken);
    }

    private async Task<ExtractionResult> ExtractFromBundles2Async(
        string gameDirectory,
        string virtualPath,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult();
        var outputDir = Path.Combine(_exportService.OutputDirectory, "extracted");

        try
        {
            // LibBundle3 的索引加载和文件读取是同步阻塞 IO，放到线程池避免卡死 UI。
            result.FilePath = await Task.Run(
                () => BundleExtractorService.ExtractFile(gameDirectory, virtualPath, outputDir),
                cancellationToken);
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"提取 {virtualPath} 失败：{ex.Message}";
            AppLogger.Instance.Warn(result.ErrorMessage);
        }

        return result;
    }

    /// <summary>
    /// GGPK 模式：通过内置 BundledGGPK 从 Content.ggpk 提取指定虚拟路径的文件到临时目录。
    /// 只读取需要的单个文件，不会提取整个 GGPK。
    /// 输出为扁平化文件名（'/' → '_'），放在 outputDir/data/ 下，与旧格式兼容。
    /// </summary>
    private async Task<ExtractionResult> ExtractFromGgpkAsync(
        string gameDirectory,
        string virtualPath,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult();
        var contentGgpk = Path.Combine(gameDirectory, "Content.ggpk");
        if (!File.Exists(contentGgpk))
        {
            result.ErrorMessage = $"未找到 Content.ggpk：{contentGgpk}";
            return result;
        }

        var outputDir = Path.Combine(_exportService.OutputDirectory, "extracted_ggpk");
        Directory.CreateDirectory(outputDir);
        // 扁平化文件名：把虚拟路径中的 '/' 替换为 '_'，放在 outputDir/data/ 下。
        // 与 ItemNameTranslator.FindDatc64Path 的查找逻辑保持一致。
        var flattenedName = virtualPath.Replace('/', '_');
        result.FilePath = Path.Combine(outputDir, "data", flattenedName);

        // 内置实现：直接通过 BundledGGPK 打开 GGPK，从 bundle 索引中提取指定虚拟路径的文件。
        // parsePathsInIndex: false + 手动 ParsePaths：Steam/Epic 版本的 _.index.bin
        // 常有 5 个左右文件 path hash 不匹配，构造器传 true 会抛异常。
        try
        {
            await Task.Run(() =>
            {
                using var ggpk = new BundledGGPK(contentGgpk, parsePathsInIndex: false);
                var failed = ggpk.Index.ParsePaths();
                if (failed > 0)
                {
                    AppLogger.Instance.Warn($"GGPK 索引解析：{failed} 个文件路径解析失败（已忽略）");
                }

                if (!ggpk.Index.TryGetFile(virtualPath, out var fileRecord) || fileRecord == null)
                {
                    throw new InvalidOperationException($"未在 GGPK 中找到文件：{virtualPath}");
                }

                var data = fileRecord.Read().ToArray();
                var dir = Path.GetDirectoryName(result.FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllBytes(result.FilePath, data);
                AppLogger.Instance.Info($"GGPK 提取成功：{virtualPath} -> {result.FilePath} ({data.Length} bytes)");
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"GGPK 提取失败：{ex.Message}";
            return result;
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// 将提取的原始 datc64 打包成 GGPK 还原 zip。
    /// zip 内条目名为游戏内虚拟路径（如 data/balance/baseitemtypes.datc64），
    /// 还原时通过 Index.Replace 按此路径写回 Content.ggpk。
    /// </summary>
    private static void CreateGgpkRestoreZip(string zipPath, string sourceDat, string virtualPath)
    {
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // 若已存在则覆盖，确保还原包始终对应最新提取的干净数据。
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        // 条目名使用 / 分隔符（GGPK 内部虚拟路径格式）。
        archive.CreateEntryFromFile(sourceDat, virtualPath.Replace('\\', '/'), CompressionLevel.Optimal);
    }

    /// <summary>
    /// 用 BundledGGPK + LibBundle3.Index.Replace 将还原 zip 中的干净 datc64 条目写回 Content.ggpk。
    /// 仅写补丁 delta，不复制整个 100GB+ 文件。
    /// </summary>
    private async Task<InstallResult> RestoreGgpkFromZipAsync(
        string gameDirectory,
        string restoreZip,
        GameModeInfo modeInfo,
        CancellationToken cancellationToken)
    {
        var ggpkPath = Path.Combine(gameDirectory, "Content.ggpk");
        var result = new InstallResult
        {
            BackupPath = restoreZip,
        };

        if (!File.Exists(ggpkPath))
        {
            result.ErrorMessage = $"未找到 Content.ggpk：{ggpkPath}";
            return result;
        }

        // 与 InstallToGgpkAsync 相同的机制：BundledGGPK + Index.Replace。
        // 区别仅在于 zip 内容：install 写入补丁后的 datc64，restore 写入原始干净 datc64。
        AppLogger.Instance.Info($"从还原包还原 GGPK（内置）：ggpk={ggpkPath}, zip={restoreZip}");
        try
        {
            int replaced = await Task.Run(() =>
            {
                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                var failedPaths = ggpk.Index.ParsePaths();
                if (failedPaths > 0)
                {
                    AppLogger.Instance.Warn($"GGPK 索引解析：{failedPaths} 个文件路径解析失败（已忽略）");
                }

                using var zip = ZipFile.OpenRead(restoreZip);
                return BundleIndex.Replace(
                    ggpk.Index,
                    zip.Entries,
                    (fileRecord, path) =>
                    {
                        var bundlePath = fileRecord.BundleRecord?.Path ?? "<unknown>";
                        AppLogger.Instance.Info($"  GGPK 还原：{path} -> size={fileRecord.Size} bundle={bundlePath}");
                        return false;
                    },
                    saveIndex: true);
            }, cancellationToken);

            AppLogger.Instance.Info($"GGPK 还原完成：替换/新增 {replaced} 个文件，ggpk={ggpkPath}");
            result.Success = true;
            result.InstalledPath = ggpkPath;
            result.GameMode = modeInfo.DisplayName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "GGPK 还原失败");
            result.ErrorMessage = $"GGPK 还原失败：{ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// 将外部特效/技能 zip 补丁应用到游戏目录。
    /// 不创建价格补丁的备份，适合技能特效等独立补丁。
    /// 根据游戏模式自动选择 GGPK 或 Bundles2 写入方式。
    /// </summary>
    public async Task<InstallResult> ApplyEffectPatchZipAsync(
        string gameDirectory,
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        var result = new InstallResult();

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            result.ErrorMessage = "游戏目录无效或未设置";
            return result;
        }

        if (!File.Exists(zipPath))
        {
            result.ErrorMessage = $"补丁文件不存在：{zipPath}";
            return result;
        }

        var modeInfo = GameModeDetector.Detect(gameDirectory);
        if (!modeInfo.IsValid)
        {
            result.ErrorMessage = modeInfo.ErrorMessage;
            return result;
        }

        result.GameMode = modeInfo.DisplayName;

        if (modeInfo.Mode == GameMode.GGPK)
        {
            var ggpkResult = await InstallEffectZipToGgpkAsync(gameDirectory, zipPath, cancellationToken);
            CollectAfterPatch();
            return ggpkResult;
        }

        var bundles2Result = await InstallEffectZipToBundles2Async(gameDirectory, zipPath, cancellationToken);
        CollectAfterPatch();
        return bundles2Result;
    }

    private async Task<InstallResult> InstallEffectZipToGgpkAsync(
        string gameDirectory,
        string zipPath,
        CancellationToken cancellationToken)
    {
        var ggpkPath = Path.Combine(gameDirectory, "Content.ggpk");
        var result = new InstallResult();

        AppLogger.Instance.Info($"应用特效补丁到 GGPK（内置）：ggpk={ggpkPath}, zip={zipPath}");
        try
        {
            int replaced = await Task.Run(() =>
            {
                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                var failedPaths = ggpk.Index.ParsePaths();
                if (failedPaths > 0)
                {
                    AppLogger.Instance.Warn($"GGPK 索引解析：{failedPaths} 个文件路径解析失败（已忽略）");
                }

                using var zip = ZipFile.OpenRead(zipPath);
                return BundleIndex.Replace(
                    ggpk.Index,
                    zip.Entries,
                    (fileRecord, path) =>
                    {
                        var bundlePath = fileRecord.BundleRecord?.Path ?? "<unknown>";
                        AppLogger.Instance.Info($"  GGPK 替换：{path} -> size={fileRecord.Size} bundle={bundlePath}");
                        return false;
                    },
                    saveIndex: true);
            }, cancellationToken);

            AppLogger.Instance.Info($"特效补丁应用完成：替换/新增 {replaced} 个文件，ggpk={ggpkPath}");
            result.Success = true;
            result.InstalledPath = ggpkPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "特效补丁应用到 GGPK 失败");
            result.ErrorMessage = $"特效补丁应用到 GGPK 失败：{ex.Message}";
        }

        return result;
    }

    private async Task<InstallResult> InstallEffectZipToBundles2Async(
        string gameDirectory,
        string zipPath,
        CancellationToken cancellationToken)
    {
        var indexBin = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");
        var result = new InstallResult();

        AppLogger.Instance.Info($"应用特效补丁到 Bundles2（内置）：index={indexBin}, zip={zipPath}");
        try
        {
            int replaced = await Task.Run(() =>
            {
                var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
                var factory = new DriveBundleFactory(bundles2Dir);
                try
                {
                    using var index = new BundleIndex(indexBin, false, factory);
                    var failedPaths = index.ParsePaths();
                    if (failedPaths > 0)
                    {
                        AppLogger.Instance.Warn($"Bundles2 索引解析：{failedPaths} 个文件路径解析失败（已忽略）");
                    }

                    using var zip = ZipFile.OpenRead(zipPath);
                    return BundleIndex.Replace(
                        index,
                        zip.Entries,
                        (fileRecord, path) =>
                        {
                            var bundlePath = fileRecord.BundleRecord?.Path ?? "<unknown>";
                            AppLogger.Instance.Info($"  Bundles2 替换：{path} -> size={fileRecord.Size} bundle={bundlePath}");
                            return false;
                        },
                        saveIndex: true);
                }
                finally
                {
                    (factory as IDisposable)?.Dispose();
                }
            }, cancellationToken);

            AppLogger.Instance.Info($"特效补丁应用完成：替换/新增 {replaced} 个文件，index={indexBin}");
            result.Success = true;
            result.InstalledPath = indexBin;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "特效补丁应用到 Bundles2 失败");
            result.ErrorMessage = $"特效补丁应用到 Bundles2 失败：{ex.Message}";
        }

        return result;
    }

    private async Task<ScriptResult> RunPythonPatchScriptAsync(
        string scriptPath,
        string sourceDat,
        string pricesCsv,
        string patchedDat,
        string gamePath,
        string outputZipPath,
        bool keepExistingPrice,
        CancellationToken cancellationToken)
    {
        var result = new ScriptResult();
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePythonPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("--source");
        psi.ArgumentList.Add(sourceDat);
        psi.ArgumentList.Add("--prices");
        psi.ArgumentList.Add(pricesCsv);
        psi.ArgumentList.Add("--patched-dat");
        psi.ArgumentList.Add(patchedDat);
        psi.ArgumentList.Add("--game-path");
        psi.ArgumentList.Add(gamePath);
        psi.ArgumentList.Add("--output-zip");
        psi.ArgumentList.Add(outputZipPath);
        psi.ArgumentList.Add("--separator");
        psi.ArgumentList.Add("");
        // 增量更新模式下传 --keep-existing-price，让 Python 脚本剥离旧价格后缀后再应用新价格，
        // 避免在已打补丁的 datc64 上叠加导致价格文字重复（如 "BaseName10D5D"）。
        if (keepExistingPrice)
        {
            psi.ArgumentList.Add("--keep-existing-price");
            AppLogger.Instance.Info("已启用 --keep-existing-price：将剥离 datc64 中已有的旧价格后缀");
        }

        AppLogger.Instance.Info($"生成补丁 datc64：{psi.FileName} {string.Join(" ", psi.ArgumentList)}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 Python 进程";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"补丁脚本执行失败：{error}";
            return result;
        }

        if (!File.Exists(patchedDat))
        {
            result.ErrorMessage = $"补丁文件未生成：{patchedDat}";
            return result;
        }

        var patchedNamesMatch = Regex.Match(output, @"patched names:\s*(\d+)");
        if (patchedNamesMatch.Success && int.TryParse(patchedNamesMatch.Groups[1].Value, out var patchedNames) && patchedNames == 0)
        {
            result.ErrorMessage = "补丁脚本未匹配到任何物品名称，请检查 prices.csv 中的物品名是否与游戏数据一致";
            return result;
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// 调用 Python 脚本的 clean 子命令：从已打补丁的 datc64 中剥离所有物价后缀，
    /// 生成"干净层"补丁 zip。用于增量更新场景下构建语义干净的迁移基线。
    /// 参考 New-CleanPhysicalRestoreZipFromPatchedSources 调用 Python --patch-scope none --strict-feature-cleanup。
    /// </summary>
    private async Task<ScriptResult> RunPythonCleanScriptAsync(
        string scriptPath,
        string sourceDat,
        string outputZip,
        string? patchedDat,
        string gamePath,
        string? reportPath,
        CancellationToken cancellationToken)
    {
        var result = new ScriptResult();
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePythonPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("clean");
        psi.ArgumentList.Add("--source");
        psi.ArgumentList.Add(sourceDat);
        psi.ArgumentList.Add("--output-zip");
        psi.ArgumentList.Add(outputZip);
        if (!string.IsNullOrEmpty(patchedDat))
        {
            psi.ArgumentList.Add("--patched-dat");
            psi.ArgumentList.Add(patchedDat);
        }
        psi.ArgumentList.Add("--game-path");
        psi.ArgumentList.Add(gamePath);
        psi.ArgumentList.Add("--separator");
        psi.ArgumentList.Add("");
        if (!string.IsNullOrEmpty(reportPath))
        {
            psi.ArgumentList.Add("--report");
            psi.ArgumentList.Add(reportPath);
        }

        AppLogger.Instance.Info($"生成干净层：{psi.FileName} {string.Join(" ", psi.ArgumentList)}");
        using var process = Process.Start(psi);
        if (process == null)
        {
            result.ErrorMessage = "无法启动 Python 进程";
            return result;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        LogProcessOutput(output, error);

        if (process.ExitCode != 0)
        {
            result.ErrorMessage = $"清理脚本执行失败：{error}";
            return result;
        }

        if (!File.Exists(outputZip))
        {
            result.ErrorMessage = $"干净层 zip 未生成：{outputZip}";
            return result;
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// 干净迁移基线（v0.4.9.3）：当增量更新模式检测到已应用价格补丁但无安全还原包时，
    /// 在沙盒中剥离所有物价标记后打包为还原包。真实游戏不会被修改。
    /// 参考 New-CleanPhysicalRestoreZipFromPatchedSources。
    ///
    /// 流程：
    /// 1. 调用 Python clean 子命令，从已打补丁的 datc64 生成"干净层"补丁 zip
    /// 2. 在沙盒中应用干净层（复用 ValidatePatchInSandboxAsync）
    /// 3. 从沙盒目录打包还原包，baseline_kind = "semantic-clean-migration"
    /// 4. 清理沙盒
    /// </summary>
    private async Task CreateCleanMigrationBaselineAsync(
        string gameDirectory,
        string bundles2Dir,
        string sourceDat,
        string baseItemsPath,
        string outputZip,
        bool isChina,
        CancellationToken cancellationToken)
    {
        var scriptPath = ResolvePatchScriptPath();
        var cleanPatchZip = Path.Combine(_exportService.OutputDirectory, "clean_price_layer.zip");
        var cleanReport = Path.Combine(_exportService.OutputDirectory, "clean_price_layer.report.json");
        var cleanPatchedDat = Path.Combine(_exportService.OutputDirectory, "baseitemtypes.clean.datc64");

        // 1. 生成干净层补丁 zip
        AppLogger.Instance.Info("干净迁移基线：正在生成干净层（剥离旧物价标记）...");
        var cleanResult = await RunPythonCleanScriptAsync(
            scriptPath,
            sourceDat,
            cleanPatchZip,
            cleanPatchedDat,
            baseItemsPath,
            cleanReport,
            cancellationToken);

        if (!cleanResult.Success)
        {
            throw new InvalidOperationException($"生成干净层失败：{cleanResult.ErrorMessage}");
        }

        // 2. 记录真实游戏的写前指纹（用于后续打包时的并发检测）
        var migrationWritePrecondition = _sandboxService.ComputeBundles2Fingerprint(bundles2Dir);
        AppLogger.Instance.Info(
            $"干净迁移基线：写前指纹 {migrationWritePrecondition.InventorySha256[..12]}..." +
            $"（{migrationWritePrecondition.Files.Count} 个文件）");

        // 3. 在沙盒中应用干净层（沙盒 = 复制 _.index.bin + LibGGPK3/，应用干净层后得到无物价标记的状态）
        AppLogger.Instance.Info("干净迁移基线：正在沙盒中应用干净层...");
        var sandboxResult = await _sandboxService.ValidatePatchInSandboxAsync(
            bundles2Dir, cleanPatchZip, cancellationToken);

        if (!sandboxResult.Success)
        {
            _sandboxService.CleanupSandbox(sandboxResult.SandboxBundles2Dir);
            throw new InvalidOperationException($"沙盒验证干净层失败：{sandboxResult.ErrorMessage}");
        }

        try
        {
            // 4. 从沙盒目录打包还原包：baseline_kind = semantic-clean-migration
            //    preconditionCheckDir = 真实游戏 Bundles2 目录（writePrecondition 来自真实游戏，需对真实游戏复核）
            var installKind = isChina ? "china" : "international";
            _sandboxService.CreateRestoreZipFromCurrentState(
                sandboxResult.SandboxBundles2Dir,
                gameDirectory,
                outputZip,
                migrationWritePrecondition,
                baselineKind: "semantic-clean-migration",
                installKind: installKind,
                targetPath: baseItemsPath,
                preconditionCheckDir: bundles2Dir);

            AppLogger.Instance.Info($"干净迁移基线已创建：{outputZip}（清理 {sandboxResult.ReplacedCount} 个文件）");
        }
        finally
        {
            _sandboxService.CleanupSandbox(sandboxResult.SandboxBundles2Dir);
        }
    }

    /// <summary>
    /// 提取英文版+目标语言版 baseitemtypes.datc64，用于 ItemNameTranslator 构建翻译表。
    /// 刷新价格时翻译表未就绪可调用此方法主动提取，无需安装补丁。
    /// </summary>
    public async Task<bool> ExtractDatc64ForTranslationAsync(
        string gameDirectory,
        GameModeInfo modeInfo,
        CancellationToken cancellationToken = default)
    {
        var enVirtualPath = "data/balance/baseitemtypes.datc64";
        var langVirtualPath = modeInfo.BaseItemsPath;

        // 英文版与目标语言版相同时（国际服英文用户）无需提取两份。
        var needEn = enVirtualPath != langVirtualPath;

        // Bundles2 模式下 BundleExtractorService 已直接输出到保留虚拟路径结构的位置。
        // GGPK 模式由内置 BundledGGPK 提取，输出扁平化文件名，无需移动。
        if (needEn)
        {
            var enResult = modeInfo.Mode == GameMode.Bundles2
                ? await ExtractFromBundles2Async(gameDirectory, enVirtualPath, cancellationToken)
                : await ExtractFromGgpkAsync(gameDirectory, enVirtualPath, cancellationToken);
            if (!enResult.Success)
            {
                AppLogger.Instance.Warn($"英文版 datc64 提取失败：{enResult.ErrorMessage}");
                return false;
            }
        }

        // 目标语言版：检查最终位置是否已存在（如安装补丁时已提取）。
        // GGPK 模式输出为扁平化文件名（/ -> _），Bundles2 模式保留原路径结构。
        var langFinalPath = modeInfo.Mode == GameMode.Bundles2
            ? Path.Combine(_exportService.OutputDirectory, "extracted",
                langVirtualPath.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(_exportService.OutputDirectory, "extracted_ggpk", "data",
                langVirtualPath.Replace('/', '_'));

        if (!File.Exists(langFinalPath))
        {
            var langResult = modeInfo.Mode == GameMode.Bundles2
                ? await ExtractFromBundles2Async(gameDirectory, langVirtualPath, cancellationToken)
                : await ExtractFromGgpkAsync(gameDirectory, langVirtualPath, cancellationToken);
            if (!langResult.Success)
            {
                AppLogger.Instance.Warn($"目标语言 datc64 提取失败：{langResult.ErrorMessage}");
                return false;
            }
        }

        AppLogger.Instance.Info($"翻译表 datc64 提取完成：英文={(needEn ? "已提取" : "同语言")}，目标语言=已就绪");
        return true;
    }

    private static string ResolvePythonPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "tools", "python", "python.exe");
        return File.Exists(bundledPath) ? bundledPath : "python";
    }

    private static string ResolvePatchScriptPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "scripts", "poe2_name_price_patch.py");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "scripts",
            "poe2_name_price_patch.py");
        projectPath = Path.GetFullPath(projectPath);

        if (File.Exists(projectPath))
        {
            return projectPath;
        }

        return bundledPath;
    }

    private static void LogProcessOutput(string output, string error)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            AppLogger.Instance.Info($"子进程输出：{output}");
        }
        if (!string.IsNullOrWhiteSpace(error))
        {
            AppLogger.Instance.Warn($"子进程错误输出：{error}");
        }
    }

    private class ExtractionResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }

    private class ScriptResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
    }
}

public class InstallResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int ExportedCount { get; set; }
    public string InstalledPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string GameMode { get; set; } = "";
}
