using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using LibBundledGGPK3;
using LibBundle3;
using BundleIndex = LibBundle3.Index;
using GgpkFileRecord = LibGGPK3.Records.FileRecord;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 泥人补丁 GGPK 模式实现：直接调用 LibBundledGGPK3 + LibBundle3 + LibGGPK3
/// 完成 GGPK 备份/还原/应用/健康检查。
///
/// - apply（应用补丁）：走 LibBundle3.Index.Replace（创建新 bundle + 重指索引 + Save 索引）
/// - restore（还原）：走 LibGGPK3.FileRecord.Write（直接覆盖 _.index.bin 字节，不碰索引对象）
/// - backup（备份）：只读 GGPK 目录树，抽出 _.index.bin + 已存在的自定义 bundle 到 zip
/// - probe（健康检查）：打开 GGPK 验证 index 结构可读
///
/// 备份策略（解决 141GB Content.ggpk 无法整文件备份的问题）：
/// - 物理上不会复制 Content.ggpk
/// - 抽出 GGPK 内当前的 Bundles2/_.index.bin（约 119MB）以及
///   （如果已存在）Bundles2/LibGGPK3/*.bundle.bin / Bundles2/TinyPoe2Smoother/*.bundle.bin
///   （Index.Replace 创建的自定义 bundle）
/// - 备份文件 = 一个 zip（与 apply/restore 模式完全兼容）
///
/// 还原策略：
/// - 通过 FileRecord.Write 直接覆盖 GGPK 内的 _.index.bin 字节
///   （不走 Index.Replace，因为 _.index.bin 是索引文件本身，不是索引内的文件）
/// - GGPK 索引回到备份时状态，补丁的 bundle 数据残留 GGPK 文件尾部（不再被索引，无副作用）
///
/// 应用策略：
/// - 把补丁 zip（条目名为游戏内虚拟路径）通过 Index.Replace 写入 GGPK
/// - 内部为每个文件创建/追加新 bundle，更新索引指向新 bundle，原始 bundle 不动
///
/// 调用前必须确保本进程已关闭对 Content.ggpk 的所有句柄（BundledGGPK 需独占写入）。
/// </summary>
internal sealed class SmootherGgpkBackupStore
{
    private readonly string _ggpkPath;
    private readonly string _backupZipPath;

    public SmootherGgpkBackupStore(string ggpkPath, string backupZipPath)
    {
        _ggpkPath = ggpkPath;
        _backupZipPath = backupZipPath;
    }

    /// <summary>备份文件完整路径（zip）。</summary>
    public string BackupPath => _backupZipPath;

    /// <summary>是否已存在备份。</summary>
    public bool HasBackup => File.Exists(_backupZipPath);

    /// <summary>删除备份文件。</summary>
    public void Remove()
    {
        if (File.Exists(_backupZipPath))
        {
            File.Delete(_backupZipPath);
        }
    }

    /// <summary>
    /// 备份当前 GGPK 的原始 index 到 zip 文件。
    /// zip 总是覆盖创建；如备份文件已存在，会被替换。
    /// </summary>
    public void Backup()
    {
        // 1. 先做 GGPK 健康检查（probe），GGPK 打不开就立刻报错，避免后面失败时
        //    旧备份被覆盖导致无法恢复。
        Probe();

        // 2. 抽出 GGPK 内的 index + 已有自定义 bundle 到 zip。
        var sw = Stopwatch.StartNew();
        AppLogger.Instance.Info($"[SmootherGgpk.Backup] ggpk={_ggpkPath}, zip={_backupZipPath}");

        var zipDir = Path.GetDirectoryName(_backupZipPath);
        if (!string.IsNullOrEmpty(zipDir))
        {
            Directory.CreateDirectory(zipDir);
        }

        using var ggpk = OpenGgpk();
        int entryCount = 0;

        using (var fs = new FileStream(_backupZipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // 1) 抽出 _.index.bin（核心文件，必须有）
            // 用 Root.TryFindNode 而非 Index.TryGetFile：
            // _.index.bin 是 GGPK 目录树中的 FileRecord（LibGGPK3.Records.FileRecord），
            // 不是 LibBundle3 索引中的 FileRecord。Index.TryGetFile 只查索引内的文件，
            // 对 _.index.bin 永远返回 false。
            if (!ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var indexNode) || indexNode is not GgpkFileRecord indexRecord)
            {
                throw new InvalidOperationException("在 GGPK 中找不到 Bundles2/_.index.bin（GGPK 可能已损坏）");
            }
            var indexBytes = indexRecord.Read();
            WriteZipEntry(zip, "Bundles2/_.index.bin", indexBytes);
            entryCount++;
            AppLogger.Instance.Info($"[SmootherGgpk.Backup] Bundles2/_.index.bin ({indexBytes.Length} bytes)");

            // 2) 抽出已存在的自定义 bundle（如果有）
            // Index.Replace 创建的 bundle 在 Bundles2/LibGGPK3/ 或 Bundles2/TinyPoe2Smoother/ 目录下。
            // 备份它们仅用于诊断/完整性；还原时只写回 _.index.bin，残留 bundle 不影响游戏。
            foreach (var bundle in ggpk.Index.Bundles.Span)
            {
                if (!bundle.Path.StartsWith("Bundles2/LibGGPK3/", StringComparison.Ordinal) &&
                    !bundle.Path.StartsWith("Bundles2/TinyPoe2Smoother/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!ggpk.Root.TryFindNode(bundle.Path, out var bundleNode) || bundleNode is not GgpkFileRecord bundleRecord)
                {
                    continue;
                }
                var data = bundleRecord.Read();
                WriteZipEntry(zip, bundle.Path, data);
                entryCount++;
                AppLogger.Instance.Info($"[SmootherGgpk.Backup] {bundle.Path} ({data.Length} bytes)");
            }
        }

        sw.Stop();
        AppLogger.Instance.Info($"[SmootherGgpk.Backup] 完成：{entryCount} 个条目，耗时 {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// 幂等备份：仅在备份文件不存在时执行 Backup()。
    /// 多次 Apply 不会重复覆盖备份（保护原始状态）。
    /// </summary>
    public void EnsureBackup()
    {
        if (HasBackup)
        {
            return;
        }
        Backup();
    }

    /// <summary>
    /// 还原：把备份 zip 写回 GGPK，把索引恢复到"备份时的状态"。
    /// </summary>
    public void Restore()
    {
        if (!HasBackup)
        {
            throw new FileNotFoundException(
                $"找不到 GGPK 备份 zip，无法还原：{_backupZipPath}");
        }

        var sw = Stopwatch.StartNew();
        AppLogger.Instance.Info($"[SmootherGgpk.Restore] ggpk={_ggpkPath}, zip={_backupZipPath}");

        // 1) 读取备份 zip 中的 _.index.bin 字节
        byte[] indexBytes;
        try
        {
            using var zip = ZipFile.OpenRead(_backupZipPath);
            var entry = zip.GetEntry("Bundles2/_.index.bin");
            if (entry == null)
            {
                throw new InvalidOperationException("还原 zip 中找不到 Bundles2/_.index.bin（备份可能为空或损坏）");
            }
            using var es = entry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            indexBytes = ms.ToArray();
            AppLogger.Instance.Info($"[SmootherGgpk.Restore] 读取备份 index：{indexBytes.Length} bytes");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"读取还原 zip 失败：{ex.Message}", ex);
        }

        // 2) 打开 GGPK，通过 Root.TryFindNode 找到 _.index.bin 的 FileRecord 并直接覆盖字节。
        // 不需要 ParsePaths：还原只通过 GGPK 目录树定位 _.index.bin，不依赖索引内容。
        // _.index.bin 是 GGPK 目录树中的 FileRecord（LibGGPK3.Records.FileRecord），
        // 不是 LibBundle3 索引中的 FileRecord，所以不能用 Index.TryGetFile。
        // FileRecord.Write 会处理大小变化（原地块移动 / 分配新空闲块），不需要 Index.Save。
        // 关键：不调用 Index.Replace 或 Index.Save —— 那会用内存中（可能已修改）的索引
        //       覆盖我们刚写回的原始字节，导致还原失效。
        using (var ggpk = OpenGgpk())
        {
            if (!ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node) || node is not GgpkFileRecord indexRecord)
            {
                throw new InvalidOperationException("在 GGPK 中找不到 Bundles2/_.index.bin");
            }
            try
            {
                indexRecord.Write(indexBytes, null);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"写入 _.index.bin 失败：{ex.Message}", ex);
            }
        }

        sw.Stop();
        AppLogger.Instance.Info($"[SmootherGgpk.Restore] 完成：已还原 _.index.bin ({indexBytes.Length} bytes)，耗时 {sw.Elapsed.TotalSeconds:F1}s");

        // 还原成功后删除备份文件（与 Bundles2 模式保持一致：成功还原后清掉备份）
        Remove();
    }

    /// <summary>
    /// 应用补丁：把补丁 zip（条目名为游戏内虚拟路径，如 "metadata/effects/.../.epk"）
    /// 通过 LibBundle3.Index.Replace 写入 Content.ggpk。
    ///
    /// Index.Replace 为每个文件创建/追加新 bundle（Bundles2/LibGGPK3/*.bundle.bin）
    /// 并更新索引指向新 bundle，原始 bundle 不动。这是 GGPK 模式下安全且高效的做法
    /// （不原地改 100GB+ 文件中的大 bundle）。
    ///
    /// 调用前必须确保本进程已关闭对 Content.ggpk 的所有句柄。
    /// </summary>
    /// <param name="patchZipPath">补丁 zip 路径，条目名为虚拟路径，条目内容为文件字节。</param>
    public void ApplyPatch(string patchZipPath)
    {
        if (!File.Exists(patchZipPath))
        {
            throw new FileNotFoundException($"补丁 zip 不存在：{patchZipPath}", patchZipPath);
        }

        var sw = Stopwatch.StartNew();
        AppLogger.Instance.Info($"[SmootherGgpk.Apply] ggpk={_ggpkPath}, zip={patchZipPath}");

        using var ggpk = OpenGgpk();
        int replaced;
        try
        {
            using var zip = ZipFile.OpenRead(patchZipPath);
            AppLogger.Instance.Info($"[SmootherGgpk.Apply] 写入 {zip.Entries.Count} 个条目...");
            // Index.Replace(Index, IEnumerable<ZipArchiveEntry>, FileCallback, bool saveIndex)
            // FileCallback = bool(FileRecord record, string path)
            // saveIndex=true：在写回文件后立刻把 _.index.bin 落盘（关键，
            //                否则 GGPK 内的索引会与新增/替换的 bundle 不一致）。
            replaced = LibBundle3.Index.Replace(
                ggpk.Index,
                zip.Entries,
                (fileRecord, path) =>
                {
                    var bundlePath = fileRecord.BundleRecord?.Path ?? "<unknown>";
                    AppLogger.Instance.Info($"  GGPK 替换：{path} -> size={fileRecord.Size} bundle={bundlePath}");
                    return false;
                },
                saveIndex: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"GGPK 应用补丁失败：{ex.Message}", ex);
        }

        sw.Stop();
        AppLogger.Instance.Info($"[SmootherGgpk.Apply] 完成：替换/新增 {replaced} 个文件，耗时 {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// 读取备份中的条目（用于诊断 / UI 展示）。
    /// 返回 [{path, length}, ...]。
    /// </summary>
    public List<(string Path, long Length)> ReadEntries()
    {
        var result = new List<(string, long)>();
        if (!HasBackup) return result;
        using var zip = ZipFile.OpenRead(_backupZipPath);
        foreach (var entry in zip.Entries)
        {
            result.Add((entry.FullName, entry.Length));
        }
        return result;
    }

    // --- 内部辅助方法 ---

    /// <summary>
    /// 打开 Content.ggpk 并执行 ParsePaths，返回可用的 BundledGGPK 实例。
    /// parsePathsInIndex: false + 手动 ParsePaths：Steam/Epic 版本的 _.index.bin
    /// 常有 5 个左右文件 path hash 不匹配，构造器传 true 会抛异常。
    /// Index.Replace 通过 zip entry 的路径 hash 在 _Files 字典里查找/新增，
    /// 少数解析失败的文件只是 Path=null，不影响 Replace 和 Save。
    /// </summary>
    private BundledGGPK OpenGgpk()
    {
        if (!File.Exists(_ggpkPath))
        {
            throw new FileNotFoundException($"Content.ggpk 不存在：{_ggpkPath}", _ggpkPath);
        }

        BundledGGPK ggpk;
        try
        {
            ggpk = new BundledGGPK(_ggpkPath, parsePathsInIndex: false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"打开 Content.ggpk 失败：{ex.Message}", ex);
        }

        var failed = ggpk.Index.ParsePaths();
        if (failed > 0)
        {
            AppLogger.Instance.Warn($"[SmootherGgpk] ParsePaths 失败 {failed} 个文件，不影响后续操作");
        }

        return ggpk;
    }

    /// <summary>
    /// GGPK 健康检查：打开 GGPK 验证 index 结构可读（bundle/file 数量非空，
    /// 且 Bundles2 目录存在）。GGPK 打不开时抛异常，调用方应捕获并阻止后续操作。
    /// </summary>
    private void Probe()
    {
        if (!File.Exists(_ggpkPath))
        {
            throw new FileNotFoundException($"Content.ggpk 不存在：{_ggpkPath}", _ggpkPath);
        }

        var info = new FileInfo(_ggpkPath);
        AppLogger.Instance.Info($"[SmootherGgpk.Probe] Content.ggpk: {_ggpkPath}");
        AppLogger.Instance.Info($"[SmootherGgpk.Probe] 文件大小：{info.Length / (1024L * 1024L * 1024L):F2} GB ({info.Length} bytes)");

        using var ggpk = OpenGgpk();

        var bundleCount = ggpk.Index.Bundles.Length;
        var fileCount = ggpk.Index.Files.Count;
        AppLogger.Instance.Info($"[SmootherGgpk.Probe] index bundle 数：{bundleCount}");
        AppLogger.Instance.Info($"[SmootherGgpk.Probe] index file 数：{fileCount}");

        if (bundleCount == 0 || fileCount == 0)
        {
            throw new InvalidOperationException("GGPK index 结构异常：bundle/file 数量为空");
        }

        var hasBundles2 = false;
        foreach (var bundle in ggpk.Index.Bundles.Span)
        {
            if (bundle.Path.StartsWith("Bundles2/", StringComparison.Ordinal))
            {
                hasBundles2 = true;
                break;
            }
        }
        if (!hasBundles2)
        {
            AppLogger.Instance.Warn("[SmootherGgpk.Probe] 未找到 Bundles2 目录");
        }
    }

    private static void WriteZipEntry(ZipArchive zip, string entryName, byte[] data)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        es.Write(data, 0, data.Length);
    }
}
