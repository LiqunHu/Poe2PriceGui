using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using LibBundle3;
using LibBundle3.Records;
using LibBundledGGPK3;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 泥人补丁服务：编排预览/应用/还原流程。
/// 移植自 tiny-poe2smoother/src/app.rs + patches.rs 的 compute_patch_set/apply_patches。
///
/// 写入策略（按游戏模式自动选择）：
/// - Bundles2 模式：直接修改 Bundles2/_.index.bin + 生成 TinyPoe2Smoother/{ordinal}.bundle.bin。
/// - GGPK 模式：进程内只读取 + 变换（不写入），收集变更后构建 patch zip，
///              关闭 GGPK 句柄，由 SmootherGgpkBackupStore.ApplyPatch 走
///              LibBundle3.Index.Replace 写回（创建新 bundle + 重指索引，不原地改
///              原始 bundle）。与 poe2_price-main 的 PatchBundledGGPK3 方式一致，
///              避免 100GB+ GGPK 上原地改大 bundle 的损坏风险。
///
/// 备份策略（按游戏模式自动选择）：
/// - Bundles2 模式：SmootherBackupStore 在 %LOCALAPPDATA%/Poe2PriceGuiData/smoother.bak 中
///                  保存应用补丁前的 _.index.bin（仅追加新条目，已存在则跳过）。
/// - GGPK 模式：SmootherGgpkBackupStore.Backup 抽出
///              GGPK 内当前的 Bundles2/_.index.bin + TinyPoe2Smoother/*.bundle.bin
///              打包成 zip（几 MB），存到 %LOCALAPPDATA%/Poe2PriceGuiData/smoother_ggpk.zip。
/// </summary>
public sealed class SmootherPatchService
{
    /// <summary>
    /// 默认相机缩放倍率（与 tiny-poe2smoother GUI 默认值一致）。
    /// </summary>
    public const double DefaultZoom = 2.4;

    private readonly BundleStore? _store;
    private readonly SmootherBackupStore? _backup;
    private readonly SmootherGgpkBackupStore? _ggpkBackup;
    private readonly string _gameDir;
    private readonly GameMode _gameMode;
    private readonly string _ggpkPath;

    /// <summary>
    /// 构造：根据游戏目录自动检测模式（GGPK vs Bundles2）。
    /// </summary>
    public SmootherPatchService(string gameDir)
    {
        _gameDir = gameDir;
        var modeInfo = GameModeDetector.Detect(gameDir);
        _gameMode = modeInfo.Mode;

        if (_gameMode == GameMode.GGPK)
        {
            _ggpkPath = Path.Combine(gameDir, "Content.ggpk");
            _ggpkBackup = new SmootherGgpkBackupStore(_ggpkPath, AppDataPath.SmootherGgpkBackup);
        }
        else
        {
            // 默认/未知模式走 Bundles2 路径（兼容旧安装）
            _store = new BundleStore(gameDir);
            _backup = new SmootherBackupStore(AppDataPath.SmootherBackup);
            _ggpkPath = "";
        }
    }

    /// <summary>
    /// 游戏目录。
    /// </summary>
    public string GameDirectory => _gameDir;

    /// <summary>
    /// 当前游戏模式（GGPK 或 Bundles2）。
    /// </summary>
    public GameMode Mode => _gameMode;

    /// <summary>
    /// 备份文件路径。
    /// - Bundles2 模式：%LOCALAPPDATA%/Poe2PriceGuiData/smoother.bak（2SBK 格式）
    /// - GGPK 模式：%LOCALAPPDATA%/Poe2PriceGuiData/smoother_ggpk.zip（zip 格式）
    /// </summary>
    public string BackupPath => _gameMode == GameMode.GGPK
        ? AppDataPath.SmootherGgpkBackup
        : _backup!.BackupPath;

    /// <summary>
    /// 是否已存在备份（即补丁已应用过）。
    /// </summary>
    public bool HasBackup => _gameMode == GameMode.GGPK
        ? _ggpkBackup!.HasBackup
        : _backup!.HasBackup;

    /// <summary>
    /// 检测游戏 index 中是否已包含 TinyPoe2Smoother/ 自定义 bundle。
    /// 对应 Rust: index.has_bundle_prefix("TinyPoe2Smoother/")。
    /// </summary>
    public bool IsPatchApplied()
    {
        if (_gameMode == GameMode.GGPK)
        {
            if (!File.Exists(_ggpkPath)) return false;
            try
            {
                // parsePathsInIndex: false —— 不解析 FileRecord.Path，只需检查 bundle 列表。
                // 这样能避开 100GB+ GGPK 的完整路径解析（30+ 秒 → 几秒），
                // 也能容忍个别解析失败的文件。
                using var ggpk = new BundledGGPK(_ggpkPath, parsePathsInIndex: false);
                foreach (var bundle in ggpk.Index.Bundles.Span)
                {
                    if (bundle.Path.StartsWith("Bundles2/TinyPoe2Smoother/", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
        if (_store == null) return false;
        if (!File.Exists(_store.IndexPath)) return false;
        var index = _store.OpenIndex();
        return index.HasBundlePrefix("Bundles2/TinyPoe2Smoother/")
            || index.HasBundlePrefix("TinyPoe2Smoother/");
    }

    /// <summary>
    /// 获取泥人补丁的详细状态：检测我们的 TinyPoe2Smoother 和别人的 TinyBundle，
    /// 统计各自修改的文件数，并抽样验证文件内容是否实际被修改。
    /// </summary>
    public SmootherDetailedStatus GetDetailedStatus()
    {
        var status = new SmootherDetailedStatus();
        if (_gameMode == GameMode.GGPK)
        {
            return GetDetailedStatusGgpk(status);
        }
        if (_store == null)
        {
            status.ErrorMessage = "内部状态异常：未初始化 BundleStore";
            return status;
        }
        if (!File.Exists(_store.IndexPath))
        {
            status.ErrorMessage = "_.index.bin 不存在";
            return status;
        }

        var index = _store.OpenIndex();

        // 检测我们的补丁（TinyPoe2Smoother/）
        var ourBundles = index.GetBundleNamesByPrefix("TinyPoe2Smoother/");
        status.OurBundleCount = ourBundles.Count;
        status.OurFileCount = index.CountFilesByBundlePrefix("TinyPoe2Smoother/");
        status.OurApplied = status.OurFileCount > 0;

        // 检测别人的补丁（TinyBundle/）
        var theirBundles = index.GetBundleNamesByPrefix("TinyBundle/");
        status.TheirBundleCount = theirBundles.Count;
        status.TheirFileCount = index.CountFilesByBundlePrefix("TinyBundle/");
        status.TheirApplied = status.TheirFileCount > 0;

        // 检测自定义 bundle 文件是否实际存在于磁盘
        status.OurBundleFilesExist = true;
        foreach (var name in ourBundles)
        {
            var path = _store.BundlePath(name);
            if (!File.Exists(path))
            {
                status.OurBundleFilesExist = false;
                status.MissingBundleFiles.Add(path);
            }
        }

        // 抽样验证：找几个 .epk 文件，检查是否已被清空（2 字节 = BOM only）
        status.SamplesChecked = 0;
        status.SamplesModified = 0;
        var samplePaths = new List<string>();
        foreach (var ip in index.IndexedPaths())
        {
            if (ip.Path.EndsWith(".epk", StringComparison.OrdinalIgnoreCase) && samplePaths.Count < 10)
            {
                samplePaths.Add(ip.Path);
            }
            if (samplePaths.Count >= 10) break;
        }
        foreach (var path in samplePaths)
        {
            try
            {
                var data = _store.ReadFile(index, path);
                status.SamplesChecked++;
                // 被清空的 .epk = 2 字节（仅 BOM）
                if (data.Length <= 2)
                {
                    status.SamplesModified++;
                }
            }
            catch
            {
                // 读取失败，跳过
            }
        }

        return status;
    }

    /// <summary>
    /// GGPK 模式的详细状态检测：直接用 LibBundle3 API 遍历 index。
    /// </summary>
    private SmootherDetailedStatus GetDetailedStatusGgpk(SmootherDetailedStatus status)
    {
        if (!File.Exists(_ggpkPath))
        {
            status.ErrorMessage = $"Content.ggpk 不存在：{_ggpkPath}";
            return status;
        }
        if (_ggpkBackup!.HasBackup)
        {
            status.OurApplied = true;
        }

        try
        {
            // parsePathsInIndex: false —— 文件路径手动按需解析，避开 100GB+ GGPK 全量
            // 解析失败时整 Index 构造抛 "Parsing path failed for N files" 的问题。
            using var ggpk = new BundledGGPK(_ggpkPath, parsePathsInIndex: false);
            var libIndex = ggpk.Index;

            // 手动解析路径，失败计数仅记录日志（少数解析失败的文件只是 Path 为 null，迭代时跳过即可）
            var failed = libIndex.ParsePaths();
            if (failed > 0)
            {
                AppLogger.Instance.Warn($"[SmootherPatch.GGPK.GetStatus] 解析 {failed} 个路径失败，将跳过这些文件");
            }

            // 统计我们的补丁
            var ourBundleNames = new List<string>();
            foreach (var bundle in libIndex.Bundles.Span)
            {
                if (bundle.Path.StartsWith("Bundles2/TinyPoe2Smoother/", StringComparison.Ordinal))
                {
                    ourBundleNames.Add(bundle.Path);
                }
            }
            status.OurBundleCount = ourBundleNames.Count;

            // 统计别人补丁
            var theirBundleNames = new List<string>();
            foreach (var bundle in libIndex.Bundles.Span)
            {
                if (bundle.Path.StartsWith("Bundles2/TinyBundle/", StringComparison.Ordinal))
                {
                    theirBundleNames.Add(bundle.Path);
                }
            }
            status.TheirBundleCount = theirBundleNames.Count;

            // 文件数（指向这些 bundle 的 fileRecord 数量）
            var ourFileCount = 0;
            var theirFileCount = 0;
            var ourBundleSet = new HashSet<string>(ourBundleNames, StringComparer.Ordinal);
            var theirBundleSet = new HashSet<string>(theirBundleNames, StringComparer.Ordinal);
            var samplePaths = new List<string>();

            foreach (var fr in libIndex.Files.Values)
            {
                var bp = fr.BundleRecord?.Path;
                if (bp == null) continue;
                if (ourBundleSet.Contains(bp))
                {
                    ourFileCount++;
                }
                else if (theirBundleSet.Contains(bp))
                {
                    theirFileCount++;
                }

                // 收集 .epk 抽样（仅原始 bundle）
                if (samplePaths.Count < 10
                    && fr.Path != null
                    && fr.Path.EndsWith(".epk", StringComparison.OrdinalIgnoreCase)
                    && !bp.StartsWith("Bundles2/TinyPoe2Smoother/", StringComparison.Ordinal)
                    && !bp.StartsWith("Bundles2/TinyBundle/", StringComparison.Ordinal))
                {
                    samplePaths.Add(fr.Path);
                }
            }
            status.OurFileCount = ourFileCount;
            status.TheirFileCount = theirFileCount;
            if (ourFileCount > 0) status.OurApplied = true;
            if (theirFileCount > 0) status.TheirApplied = true;

            // GGPK 模式下 bundle 数据本身就在 GGPK 内，所以"bundle 文件存在"恒为 true
            status.OurBundleFilesExist = ourBundleNames.Count > 0;

            // 抽样验证
            status.SamplesChecked = 0;
            status.SamplesModified = 0;
            foreach (var samplePath in samplePaths)
            {
                try
                {
                    if (!libIndex.TryGetFile(samplePath, out var fr) || fr == null) continue;
                    var data = fr.Read();
                    status.SamplesChecked++;
                    if (data.Length <= 2)
                    {
                        status.SamplesModified++;
                    }
                }
                catch
                {
                    // 读取失败跳过
                }
            }
        }
        catch (Exception ex)
        {
            status.ErrorMessage = $"GGPK 状态检测失败：{ex.Message}";
        }

        return status;
    }

    #region 预览（不写入）

    /// <summary>
    /// 预览：计算给定补丁列表会修改哪些文件，返回报告但不写入磁盘。
    /// </summary>
    public SmootherPatchReport Preview(IReadOnlyList<PatchId> patches, double zoom = DefaultZoom, IProgress<SmootherProgress>? progress = null)
    {
        return ComputeReport(patches, zoom, apply: false, progress);
    }

    #endregion

    #region 应用（写入）

    /// <summary>
    /// 应用：备份当前 index，然后写入补丁。
    /// 对应 Rust: apply_patches(game_dir, patches, zoom)。
    /// </summary>
    public SmootherPatchReport Apply(IReadOnlyList<PatchId> patches, double zoom = DefaultZoom, IProgress<SmootherProgress>? progress = null)
    {
        return ComputeReport(patches, zoom, apply: true, progress);
    }

    #endregion

    #region 还原

    /// <summary>
    /// 还原：从备份恢复 index。
    /// 对应 Rust: BackupStore::restore(game_dir)。
    /// </summary>
    /// <returns>还原的文件数（通常为 1，即 _.index.bin）。</returns>
    public int Restore()
    {
        if (_gameMode == GameMode.GGPK)
        {
            // GGPK 模式：通过 FileRecord.Write 把备份 zip 中的 _.index.bin 字节直接写回 GGPK。
            _ggpkBackup!.Restore();
            return 1;
        }
        return _backup!.Restore(_store!.GameDirectory);
    }

    /// <summary>
    /// 删除备份文件（不还原游戏文件）。
    /// 通常在用户确认游戏已手动还原后调用。
    /// </summary>
    public void ClearBackup()
    {
        if (_gameMode == GameMode.GGPK)
        {
            _ggpkBackup!.Remove();
        }
        else
        {
            _backup!.Remove();
        }
    }

    #endregion

    #region 内部实现

    /// <summary>
    /// 计算补丁报告，可选是否写入磁盘。
    /// 对应 Rust: compute_patch_set + (apply 时) apply_bundle_replacements。
    /// progress 用于向 UI 报告执行进度（可选）。
    /// </summary>
    private SmootherPatchReport ComputeReport(IReadOnlyList<PatchId> patches, double zoom, bool apply, IProgress<SmootherProgress>? progress = null)
    {
        var report = new SmootherPatchReport { Success = false };
        try
        {
            if (_gameMode == GameMode.GGPK)
            {
                return ComputeReportGgpk(patches, zoom, apply, progress);
            }
            return ComputeReportBundles2(patches, zoom, apply, progress);
        }
        catch (Exception ex)
        {
            report.ErrorMessage = ex.Message;
            progress?.Report(new SmootherProgress { Description = $"出错：{ex.Message}", Percent = 100 });
            return report;
        }
    }

    /// <summary>
    /// Bundles2 模式的 ComputeReport（原始实现）。
    /// </summary>
    private SmootherPatchReport ComputeReportBundles2(IReadOnlyList<PatchId> patches, double zoom, bool apply, IProgress<SmootherProgress>? progress)
    {
        var report = new SmootherPatchReport { Success = false };
        if (_store == null || _backup == null)
        {
            throw new InvalidOperationException("SmootherPatchService 内部状态异常：未初始化 BundleStore / Backup。");
        }

        progress?.Report(new SmootherProgress { Description = "正在打开索引文件...", Percent = 5 });

        var patchList = UniquePatches(patches);
        var index = _store.OpenIndex();

        // 1. 收集候选文件
        progress?.Report(new SmootherProgress { Description = "正在收集候选文件...", Percent = 10 });
        var candidates = CollectPatchTargets(index, patchList);
        candidates = DedupCandidates(candidates);

        if (candidates.Count == 0)
        {
            report.Success = true;
            report.ChangedFileCount = 0;
            progress?.Report(new SmootherProgress { Description = "完成（无候选文件）", Percent = 100 });
            return report;
        }

        progress?.Report(new SmootherProgress { Description = $"已收集 {candidates.Count} 个候选文件，正在读取 bundle...", Percent = 25 });

        // 2. 批量读取相关 bundle
        var bundleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, file) in candidates)
        {
            bundleNames.Add(file.BundleName);
        }
        var bundles = _store.ReadBundlesBatch(bundleNames);

        // 3. 切片每个候选文件
        progress?.Report(new SmootherProgress { Description = $"正在切片 {candidates.Count} 个文件...", Percent = 40 });
        var fileData = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, file) in candidates)
        {
            if (!bundles.TryGetValue(file.BundleName, out var bundleData))
            {
                throw new InvalidOperationException($"bundle 已批量加载但缺失：{file.BundleName}");
            }
            fileData[path] = BundleStore.SliceFile(bundleData, file);
        }

        // 4. 对每个候选文件应用所有匹配的补丁（最耗时阶段，按比例报告进度）
        progress?.Report(new SmootherProgress { Description = "正在应用补丁变换...", Percent = 50 });
        var replacements = new Dictionary<string, List<(BundleFile File, byte[] Data)>>(StringComparer.Ordinal);
        var changes = new List<PatchChange>();
        var patchHits = new Dictionary<PatchId, int>();

        // 报告频率：每 500 个或每 5% 报告一次
        var reportInterval = Math.Max(500, candidates.Count / 20);
        var processed = 0;
        var transformStartPercent = 50;
        var transformEndPercent = 85;

        foreach (var (path, file) in candidates)
        {
            var bytes = fileData[path];
            var current = bytes;
            var changed = false;

            foreach (var patch in patchList)
            {
                if (!PatchCatalog.PatchAppliesPath(patch, path)) continue;

                var after = PatchTransforms.Transform(patch, path, current, zoom);
                if (after != current)
                {
                    current = after;
                    changed = true;
                    patchHits[patch] = patchHits.TryGetValue(patch, out var c) ? c + 1 : 1;
                }
            }

            if (changed)
            {
                changes.Add(new PatchChange
                {
                    Path = path,
                    BundleName = file.BundleName,
                    OldSize = bytes.Length,
                    NewSize = current.Length,
                });

                if (!replacements.TryGetValue(file.BundleName, out var list))
                {
                    list = new List<(BundleFile, byte[])>();
                    replacements[file.BundleName] = list;
                }
                list.Add((file, current));
            }

            processed++;
            if (processed % reportInterval == 0)
            {
                var ratio = (double)processed / candidates.Count;
                var percent = transformStartPercent + (int)((transformEndPercent - transformStartPercent) * ratio);
                progress?.Report(new SmootherProgress
                {
                    Description = $"正在变换文件 {processed}/{candidates.Count}...",
                    Percent = percent
                });
            }
        }

        report.Changes = changes;
        report.ChangedFileCount = changes.Count;
        report.PatchHitCounts = patchHits;

        // 5. 写入磁盘（仅 apply=true 时）
        if (apply && replacements.Count > 0)
        {
            progress?.Report(new SmootherProgress { Description = "正在备份并写入磁盘...", Percent = 90 });
            // 备份当前 _.index.bin（如果还没有备份过）
            _backup.EnsureOriginals(_store.GameDirectory, new[] { "Bundles2/_.index.bin" });

            _store.ApplyBundleReplacements(index, replacements);
        }

        report.Success = true;
        progress?.Report(new SmootherProgress { Description = "完成", Percent = 100 });
        return report;
    }

    /// <summary>
    /// GGPK 模式的 ComputeReport：进程内只读取 + 变换，写回委托给
    /// SmootherGgpkBackupStore.ApplyPatch（走 LibBundle3.Index.Replace）。
    ///
    /// 流程：
    /// 1. （apply 时）先备份原始 _.index.bin（必须在打开 GGPK 前，
    ///    避免文件锁竞争）
    /// 2. 进程内打开 BundledGGPK（parsePathsInIndex=false），手动 ParsePaths
    /// 3. 收集候选文件：复用 PatchCatalog.PatchTargetsPath / ExactPatchTargets
    /// 4. 逐个 FileRecord.Read() 拿到字节，调用 PatchTransforms.Transform，
    ///    收集 {path: newBytes}（只读，不调用 FileRecord.Write）
    /// 5. 关闭 GGPK 句柄（using 块结束）
    /// 6. （apply 时）把变更打包成 patch zip，通过 Index.Replace
    ///    写回：创建新 bundle + 重指索引，不原地改原始 bundle。
    ///
    /// 为何不原地 Write：参考 poe2_price-main 的 PatchBundledGGPK3 工具，
    /// GGPK 模式下应通过 Index.Replace 创建新 bundle，避免在 100GB+ 文件中
    /// 原地解压/重压大 bundle 的损坏风险与性能开销。
    /// </summary>
    private SmootherPatchReport ComputeReportGgpk(IReadOnlyList<PatchId> patches, double zoom, bool apply, IProgress<SmootherProgress>? progress)
    {
        var report = new SmootherPatchReport { Success = false };
        var sw = Stopwatch.StartNew();

        if (!File.Exists(_ggpkPath))
        {
            report.ErrorMessage = $"Content.ggpk 不存在：{_ggpkPath}";
            return report;
        }

        // ============ 备份阶段：必须先于打开 GGPK ============
        // 关键：LibGGPK3 打开 Content.ggpk 时用了 FileShare.Read，但子进程工具体积
        // 较大、初始化较慢，期间如果主进程同时持有文件句柄，Windows 文件锁竞争
        // 会让工具的 File.Exists / new BundledGGPK 都失败（"being used by another process"）。
        // 所以把 backup 放到最前面：主进程尚未打开 GGPK 时，让工具独占访问。
        if (apply)
        {
            progress?.Report(new SmootherProgress { Description = "正在备份原始 index...", Percent = 5 });
            try
            {
                _ggpkBackup!.EnsureBackup();
            }
            catch (Exception ex)
            {
                report.ErrorMessage = $"备份 Content.ggpk 失败：{ex.Message}";
                return report;
            }
        }

        // ============ 读取 + 变换阶段（进程内，只读不写） ============
        progress?.Report(new SmootherProgress { Description = "正在打开 Content.ggpk...", Percent = 8 });

        // 收集变更：path -> newBytes。在 using 块外声明，便于关闭 GGPK 后再打包写回。
        var modifiedFiles = new List<(string Path, byte[] Data)>();
        var changes = new List<PatchChange>();
        var patchHits = new Dictionary<PatchId, int>();
        var candidateCount = 0;

        try
        {
            // parsePathsInIndex: false —— 手动按需 ParsePaths，避开 "Parsing path failed
            // for N files" 异常；少数解析失败的文件只是 Path 为 null，迭代时跳过即可。
            using var ggpk = new BundledGGPK(_ggpkPath, parsePathsInIndex: false);
            var libIndex = ggpk.Index;
            var patchList = UniquePatches(patches);

            // 手动解析路径（必要，否则 FileRecord.Path 为 null）；失败计数仅记录日志
            var failed = libIndex.ParsePaths();
            if (failed > 0)
            {
                AppLogger.Instance.Warn($"[SmootherPatch.GGPK] 解析 {failed} 个路径失败，将跳过这些文件");
            }

            progress?.Report(new SmootherProgress { Description = "正在收集候选文件...", Percent = 10 });
            var candidates = CollectPatchTargetsGgpk(libIndex, patchList);
            candidates = DedupCandidatesGgpk(candidates);
            candidateCount = candidates.Count;

            if (candidates.Count == 0)
            {
                report.Success = true;
                report.ChangedFileCount = 0;
                progress?.Report(new SmootherProgress { Description = "完成（无候选文件）", Percent = 100 });
                return report;
            }

            progress?.Report(new SmootherProgress
            {
                Description = $"已收集 {candidates.Count} 个候选文件，开始变换...",
                Percent = 25
            });

            // 逐个读取 + 变换 + 收集变更（不写回）
            var reportInterval = Math.Max(500, candidates.Count / 20);
            var processed = 0;
            const int transformStartPercent = 30;
            const int transformEndPercent = 80;

            foreach (var (path, fr) in candidates)
            {
                byte[] current;
                try
                {
                    current = fr.Read().ToArray();
                }
                catch (Exception ex)
                {
                    AppLogger.Instance.Warn($"[SmootherPatch.GGPK] 读取 {path} 失败：{ex.Message}，跳过");
                    processed++;
                    continue;
                }
                var original = current;
                var changed = false;

                foreach (var patch in patchList)
                {
                    if (!PatchCatalog.PatchAppliesPath(patch, path)) continue;

                    var after = PatchTransforms.Transform(patch, path, current, zoom);
                    if (after != current)
                    {
                        current = after;
                        changed = true;
                        patchHits[patch] = patchHits.TryGetValue(patch, out var c) ? c + 1 : 1;
                    }
                }

                if (changed)
                {
                    changes.Add(new PatchChange
                    {
                        Path = path,
                        BundleName = fr.BundleRecord?.Path ?? "",
                        OldSize = original.Length,
                        NewSize = current.Length,
                    });
                    // 只记 path + bytes，不持有 FileRecord（using 块结束后 FileRecord 失效）
                    modifiedFiles.Add((path, current));
                }

                processed++;
                if (processed % reportInterval == 0)
                {
                    var ratio = (double)processed / candidates.Count;
                    var percent = transformStartPercent + (int)((transformEndPercent - transformStartPercent) * ratio);
                    progress?.Report(new SmootherProgress
                    {
                        Description = $"正在变换文件 {processed}/{candidates.Count}...",
                        Percent = percent
                    });
                }
            }
        }
        catch (Exception ex)
        {
            report.ErrorMessage = $"读取/变换 Content.ggpk 失败：{ex.Message}";
            AppLogger.Instance.Error($"[SmootherPatch.GGPK] 读取/变换失败：{ex}");
            return report;
        }

        report.Changes = changes;
        report.ChangedFileCount = changes.Count;
        report.PatchHitCounts = patchHits;

        if (!apply || modifiedFiles.Count == 0)
        {
            report.Success = true;
            progress?.Report(new SmootherProgress
            {
                Description = modifiedFiles.Count == 0 ? "完成（无文件被修改）" : "预览完成（未应用）",
                Percent = 100
            });
            sw.Stop();
            AppLogger.Instance.Info($"[SmootherPatch.GGPK] {(apply ? "应用" : "预览")}完成：扫描 {candidateCount}，变更 {changes.Count}，耗时 {sw.Elapsed.TotalSeconds:F1}s");
            return report;
        }

        // ============ 写回阶段：构建 patch zip + 调用 --apply 工具 ============
        // GGPK 句柄已在上面 using 块中关闭，工具可独占写入。
        // 走 Index.Replace（创建新 bundle + 重指索引），不原地改原始 bundle。
        progress?.Report(new SmootherProgress
        {
            Description = $"正在构建补丁包（{modifiedFiles.Count} 个文件）...",
            Percent = 85
        });

        string? patchZip = null;
        try
        {
            patchZip = BuildPatchZip(modifiedFiles);
            progress?.Report(new SmootherProgress { Description = "正在写入 Content.ggpk...", Percent = 92 });
            _ggpkBackup!.ApplyPatch(patchZip);
        }
        catch (Exception ex)
        {
            report.ErrorMessage = $"写入 Content.ggpk 失败：{ex.Message}。请尝试用\"还原\"功能恢复到应用前的状态。";
            AppLogger.Instance.Error($"[SmootherPatch.GGPK] 写回失败：{ex}");
            return report;
        }
        finally
        {
            if (patchZip != null && File.Exists(patchZip))
            {
                try { File.Delete(patchZip); } catch { /* 临时文件清理失败不影响主流程 */ }
            }
        }

        report.Success = true;
        progress?.Report(new SmootherProgress
        {
            Description = $"完成：变更 {changes.Count} 个文件，耗时 {sw.Elapsed.TotalSeconds:F1}s",
            Percent = 100
        });
        AppLogger.Instance.Info($"[SmootherPatch.GGPK] 应用完成：扫描 {candidateCount}，写入 {modifiedFiles.Count}，耗时 {sw.Elapsed.TotalSeconds:F1}s");
        return report;
    }

    /// <summary>
    /// 把变更文件列表打包成补丁 zip：条目名为游戏内虚拟路径（'/' 分隔），
    /// 条目内容为文件字节。供 SmootherGgpkBackupStore.ApplyPatch 通过
    /// LibBundle3.Index.Replace 写入 GGPK。
    /// </summary>
    private static string BuildPatchZip(List<(string Path, byte[] Data)> files)
    {
        // 放到 AppData 根目录下，避免临时目录权限问题；文件名带 GUID 防冲突。
        var dir = AppDataPath.Root;
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"smoother_patch_{Guid.NewGuid():N}.zip");

        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (path, data) in files)
        {
            // CompressionLevel.Fastest：补丁多为小文件（清空的 .epk / 精简的 .ao），
            // 用最快压缩即可；Index.Replace 按条目名（虚拟路径）匹配，与压缩级别无关。
            var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
            using var es = entry.Open();
            es.Write(data);
        }
        return zipPath;
    }

    /// <summary>
    /// 收集所有补丁的目标文件（GGPK 版本）。
    /// </summary>
    private static List<(string Path, FileRecord File)> CollectPatchTargetsGgpk(
        LibBundle3.Index index,
        IReadOnlyList<PatchId> patches)
    {
        var targets = new List<(string, FileRecord)>();
        foreach (var patch in patches)
        {
            // Minimap/AtlasFog 使用精确路径
            var exact = PatchCatalog.ExactPatchTargets(patch);
            if (exact.Length > 0)
            {
                foreach (var path in exact)
                {
                    if (index.TryGetFile(path, out var fr) && fr != null)
                    {
                        targets.Add((path, fr));
                    }
                }
                continue;
            }

            // 其他补丁遍历所有索引路径，按 patch_applies_path 筛选
            foreach (var fr in index.Files.Values)
            {
                if (string.IsNullOrEmpty(fr.Path)) continue;
                if (PatchCatalog.PatchTargetsPath(patch, fr.Path))
                {
                    targets.Add((fr.Path, fr));
                }
            }
        }
        return targets;
    }

    /// <summary>
    /// 候选去重（GGPK 版本）。
    /// </summary>
    private static List<(string Path, FileRecord File)> DedupCandidatesGgpk(
        List<(string Path, FileRecord File)> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, FileRecord)>(candidates.Count);
        foreach (var item in candidates)
        {
            if (seen.Add(item.Path))
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// 收集所有补丁的目标文件（Bundles2 版本）。
    /// 对应 Rust: collect_patch_targets(index, patches)。
    /// </summary>
    private static List<(string Path, BundleFile File)> CollectPatchTargets(
        BundleIndex index,
        IReadOnlyList<PatchId> patches)
    {
        var targets = new List<(string, BundleFile)>();
        foreach (var patch in patches)
        {
            // Minimap/AtlasFog 使用精确路径
            var exact = PatchCatalog.ExactPatchTargets(patch);
            if (exact.Length > 0)
            {
                foreach (var path in exact)
                {
                    var file = index.FileByPath(path);
                    if (file != null)
                    {
                        targets.Add((path, file));
                    }
                }
                continue;
            }

            // 其他补丁遍历所有索引路径，按 patch_applies_path 筛选
            foreach (var ip in index.IndexedPaths())
            {
                if (PatchCatalog.PatchTargetsPath(patch, ip.Path))
                {
                    targets.Add((ip.Path, ip.File));
                }
            }
        }
        return targets;
    }

    /// <summary>
    /// 候选去重：相同路径只保留首次出现。
    /// 对应 Rust: dedup_candidates(candidates)。
    /// </summary>
    private static List<(string Path, BundleFile File)> DedupCandidates(
        List<(string Path, BundleFile File)> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, BundleFile)>(candidates.Count);
        foreach (var item in candidates)
        {
            if (seen.Add(item.Path))
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// 补丁去重：保持首次出现的顺序。
    /// 对应 Rust: unique_patches(patches)。
    /// </summary>
    private static List<PatchId> UniquePatches(IReadOnlyList<PatchId> patches)
    {
        var seen = new HashSet<PatchId>();
        var result = new List<PatchId>(patches.Count);
        foreach (var p in patches)
        {
            if (seen.Add(p))
            {
                result.Add(p);
            }
        }
        return result;
    }

    #endregion
}
