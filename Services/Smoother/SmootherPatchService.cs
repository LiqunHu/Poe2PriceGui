using System.IO;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 泥人补丁服务：编排预览/应用/还原流程。
/// 移植自 tiny-poe2smoother/src/app.rs + patches.rs 的 compute_patch_set/apply_patches。
///
/// 写入策略：直接修改 Bundles2/_.index.bin + 生成 TinyPoe2Smoother/{ordinal}.bundle.bin。
/// 备份策略：SmootherBackupStore 在 %LOCALAPPDATA%/Poe2PriceGui/smoother.bak 中
///          保存应用补丁前的 _.index.bin（仅追加新条目，已存在则跳过）。
/// </summary>
public sealed class SmootherPatchService
{
    /// <summary>
    /// 默认相机缩放倍率（与 tiny-poe2smoother GUI 默认值一致）。
    /// </summary>
    public const double DefaultZoom = 2.4;

    private readonly BundleStore _store;
    private readonly SmootherBackupStore _backup;

    public SmootherPatchService(string gameDir)
    {
        _store = new BundleStore(gameDir);
        _backup = new SmootherBackupStore();
    }

    /// <summary>
    /// 游戏目录。
    /// </summary>
    public string GameDirectory => _store.GameDirectory;

    /// <summary>
    /// 备份文件路径。
    /// </summary>
    public string BackupPath => _backup.BackupPath;

    /// <summary>
    /// 是否已存在备份（即补丁已应用过）。
    /// </summary>
    public bool HasBackup => _backup.HasBackup;

    /// <summary>
    /// 检测游戏 index 中是否已包含 TinyPoe2Smoother/ 自定义 bundle。
    /// 对应 Rust: index.has_bundle_prefix("TinyPoe2Smoother/")。
    /// </summary>
    public bool IsPatchApplied()
    {
        if (!File.Exists(_store.IndexPath)) return false;
        var index = _store.OpenIndex();
        return index.HasBundlePrefix("TinyPoe2Smoother/");
    }

    /// <summary>
    /// 获取泥人补丁的详细状态：检测我们的 TinyPoe2Smoother 和别人的 TinyBundle，
    /// 统计各自修改的文件数，并抽样验证文件内容是否实际被修改。
    /// </summary>
    public SmootherDetailedStatus GetDetailedStatus()
    {
        var status = new SmootherDetailedStatus();
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
                status.MissingBundleFiles.Add(name);
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
    /// 应用：备份当前 _.index.bin，然后写入补丁。
    /// 对应 Rust: apply_patches(game_dir, patches, zoom)。
    /// </summary>
    public SmootherPatchReport Apply(IReadOnlyList<PatchId> patches, double zoom = DefaultZoom, IProgress<SmootherProgress>? progress = null)
    {
        return ComputeReport(patches, zoom, apply: true, progress);
    }

    #endregion

    #region 还原

    /// <summary>
    /// 还原：从备份恢复 _.index.bin，并删除 TinyPoe2Smoother/ 目录。
    /// 对应 Rust: BackupStore::restore(game_dir)。
    /// </summary>
    /// <returns>还原的文件数（通常为 1，即 _.index.bin）。</returns>
    public int Restore()
    {
        return _backup.Restore(_store.GameDirectory);
    }

    /// <summary>
    /// 删除备份文件（不还原游戏文件）。
    /// 通常在用户确认游戏已手动还原后调用。
    /// </summary>
    public void ClearBackup()
    {
        _backup.Remove();
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
        catch (Exception ex)
        {
            report.ErrorMessage = ex.Message;
            progress?.Report(new SmootherProgress { Description = $"出错：{ex.Message}", Percent = 100 });
            return report;
        }
    }

    /// <summary>
    /// 收集所有补丁的目标文件。
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
