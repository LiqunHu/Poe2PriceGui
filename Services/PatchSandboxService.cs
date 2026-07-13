using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibBundle3;
using BundleIndex = LibBundle3.Index;

namespace Poe2PriceGui.Services;

/// <summary>
/// Bundles2 状态指纹：记录 _.index.bin 及 LibGGPK3/ 下所有文件的路径、大小、SHA256。
/// 用于写前并发检测和写后读回校验，参考 poe2_price v0.4.9.4 的 Get-Poe2Bundles2MutationFingerprint。
/// </summary>
public class Bundles2Fingerprint
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = "";

    [JsonPropertyName("files")]
    public List<FileFingerprint> Files { get; set; } = new();

    [JsonPropertyName("inventory_sha256")]
    public string InventorySha256 { get; set; } = "";
}

public class FileFingerprint
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("last_write_time_utc")]
    public string LastWriteTimeUtc { get; set; } = "";

    [JsonPropertyName("crc32")]
    public string Crc32 { get; set; } = "";
}

/// <summary>
/// 沙盒验证结果。
/// </summary>
public class SandboxValidationResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int ReplacedCount { get; set; }
    public string SandboxBundles2Dir { get; set; } = "";
    public Bundles2Fingerprint PostPatchFingerprint { get; set; } = new();
}

/// <summary>
/// 真实还原包 manifest（v2 格式），参考 poe2_price v0.4.9.4 的 New-PhysicalRestoreZip。
/// </summary>
public class PhysicalRestoreManifest
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "poe2-price-patch-physical-restore";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("install_kind")]
    public string InstallKind { get; set; } = "";

    [JsonPropertyName("target_path")]
    public string TargetPath { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "Bundles2";

    [JsonPropertyName("baseline_kind")]
    public string BaselineKind { get; set; } = "byte-exact-prepatch";

    [JsonPropertyName("base_fingerprint")]
    public Bundles2Fingerprint? BaseFingerprint { get; set; }

    [JsonPropertyName("write_precondition")]
    public Bundles2Fingerprint? WritePrecondition { get; set; }

    [JsonPropertyName("restore_files")]
    public List<FileFingerprint> RestoreFiles { get; set; } = new();
}

/// <summary>
/// 沙盒验证服务：在临时沙盒中验证补丁，确保成功后才写入真实游戏。
/// 实现 v0.4.9.4 的"隔离构建、完整校验后原子发布"和 v0.4.9.3 的"沙盒迁移基线"安全链路。
///
/// 核心安全链路：
/// 1. 写前并发指纹：记录写入前 Bundles2 状态，写入前复核，检测并发修改
/// 2. 沙盒验证：将 _.index.bin 和 LibGGPK3/ 复制到临时沙盒，在沙盒上应用补丁并校验
/// 3. 写后读回校验：写入真实游戏后重新计算指纹，确认写入成功
/// 4. 自动回滚：写后校验失败时从备份还原
/// 5. 还原包生成：当增量更新且无安全还原包时，从当前状态生成还原包
/// </summary>
public class PatchSandboxService
{
    private static readonly string[] IndexFiles =
        { "_.index.bin", "_.index.high.bin", "_.index.low.bin", ".index.dbg" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 计算 Bundles2 状态指纹：_.index.bin + _.index.high.bin + _.index.low.bin + .index.dbg + LibGGPK3/ 所有文件的 SHA256。
    /// 按路径排序后组合计算 inventory_sha256，用于检测任意文件的新增、删除或内容变化。
    /// </summary>
    public Bundles2Fingerprint ComputeBundles2Fingerprint(string bundles2Dir)
    {
        var rootPrefix = Path.GetFullPath(bundles2Dir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var files = new List<FileFingerprint>();

        foreach (var name in IndexFiles)
        {
            var path = Path.Combine(bundles2Dir, name);
            if (File.Exists(path))
            {
                files.Add(CreateFileFingerprint(path, rootPrefix));
            }
        }

        var libDir = Path.Combine(bundles2Dir, "LibGGPK3");
        if (Directory.Exists(libDir))
        {
            foreach (var file in Directory.GetFiles(libDir, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(CreateFileFingerprint(file, rootPrefix));
            }
        }

        var sorted = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var canonical = string.Join("\n", sorted.Select(f =>
            $"{f.Path.ToLowerInvariant()}|{f.Length}|{f.Sha256.ToLowerInvariant()}"));

        return new Bundles2Fingerprint
        {
            Version = 1,
            Algorithm = "path-length-sha256-v1",
            Files = sorted,
            InventorySha256 = ComputeSha256(canonical),
        };
    }

    /// <summary>
    /// 验证当前 Bundles2 状态与预期指纹一致。
    /// 不一致说明有并发修改（如游戏更新、其他工具写入），应中止写入。
    /// </summary>
    public bool ValidateFingerprint(Bundles2Fingerprint expected, string bundles2Dir)
    {
        var current = ComputeBundles2Fingerprint(bundles2Dir);
        if (expected.Version != 1 || expected.Algorithm != "path-length-sha256-v1")
        {
            AppLogger.Instance.Warn($"指纹算法不匹配：expected v{expected.Version}/{expected.Algorithm}");
            return false;
        }
        if (expected.Files.Count != current.Files.Count)
        {
            AppLogger.Instance.Warn($"文件数量变化：expected {expected.Files.Count}，current {current.Files.Count}");
            return false;
        }
        return string.Equals(
            expected.InventorySha256,
            current.InventorySha256,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在沙盒中验证补丁：复制 _.index.bin 和 LibGGPK3/ 到临时目录，
    /// 在临时索引上应用补丁，验证应用成功。
    /// 成功前不修改真实游戏文件。参考 New-CleanPhysicalRestoreZipFromPatchedSources 的沙盒策略。
    /// </summary>
    public async Task<SandboxValidationResult> ValidatePatchInSandboxAsync(
        string bundles2Dir,
        string patchZip,
        CancellationToken cancellationToken = default)
    {
        var result = new SandboxValidationResult();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"poe2_sandbox_{Guid.NewGuid():N}");
        var sandboxBundles2 = Path.Combine(tempRoot, "Bundles2");

        try
        {
            Directory.CreateDirectory(sandboxBundles2);
            AppLogger.Instance.Info($"沙盒验证：创建临时沙盒 {sandboxBundles2}");

            // 复制索引文件到沙盒（与参考项目一致：只复制 index 和 LibGGPK3，不复制顶层 bundle）
            foreach (var name in IndexFiles)
            {
                var src = Path.Combine(bundles2Dir, name);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(sandboxBundles2, name), overwrite: true);
                }
            }

            var sandboxIndex = Path.Combine(sandboxBundles2, "_.index.bin");
            if (!File.Exists(sandboxIndex))
            {
                result.ErrorMessage = "沙盒验证失败：_.index.bin 不存在";
                return result;
            }

            // 复制 LibGGPK3/ 目录到沙盒（Index.Replace 会在此写入新 bundle）
            var libDir = Path.Combine(bundles2Dir, "LibGGPK3");
            if (Directory.Exists(libDir))
            {
                CopyDirectory(libDir, Path.Combine(sandboxBundles2, "LibGGPK3"));
            }

            // 记录沙盒写入前指纹
            var preSandboxFingerprint = ComputeBundles2Fingerprint(sandboxBundles2);

            // 在沙盒中应用补丁
            int replaced;
            try
            {
                replaced = await Task.Run(() =>
                {
                    var factory = new DriveBundleFactory(sandboxBundles2);
                    try
                    {
                        using var index = new BundleIndex(sandboxIndex, false, factory);
                        var failedPaths = index.ParsePaths();
                        if (failedPaths > 0)
                        {
                            AppLogger.Instance.Warn($"沙盒索引解析：{failedPaths} 个文件路径解析失败（已忽略）");
                        }

                        using var zip = ZipFile.OpenRead(patchZip);
                        return BundleIndex.Replace(
                            index,
                            zip.Entries,
                            (fileRecord, path) =>
                            {
                                AppLogger.Instance.Info($"  沙盒替换：{path} -> size={fileRecord.Size}");
                                return false;
                            },
                            saveIndex: true);
                    }
                    finally
                    {
                        (factory as IDisposable)?.Dispose();
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"沙盒验证失败：补丁应用异常：{ex.Message}";
                return result;
            }

            if (replaced <= 0)
            {
                result.ErrorMessage = $"沙盒验证失败：补丁未替换任何文件（replaced={replaced}）";
                return result;
            }

            // 验证沙盒 _.index.bin 已被修改（指纹变化确认写入生效）
            var postSandboxFingerprint = ComputeBundles2Fingerprint(sandboxBundles2);
            if (string.Equals(
                    preSandboxFingerprint.InventorySha256,
                    postSandboxFingerprint.InventorySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                result.ErrorMessage = "沙盒验证失败：应用补丁后指纹未变化，写入可能未生效";
                return result;
            }

            AppLogger.Instance.Info($"沙盒验证通过：替换 {replaced} 个文件，沙盒状态已变化");

            result.Success = true;
            result.ReplacedCount = replaced;
            result.SandboxBundles2Dir = sandboxBundles2;
            result.PostPatchFingerprint = postSandboxFingerprint;
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"沙盒验证异常：{ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// 清理沙盒临时目录。
    /// </summary>
    public void CleanupSandbox(string sandboxBundles2Dir)
    {
        if (string.IsNullOrEmpty(sandboxBundles2Dir) || !Directory.Exists(sandboxBundles2Dir))
        {
            return;
        }

        // 沙盒目录结构为 tempRoot/Bundles2，清理 tempRoot
        var tempRoot = Path.GetDirectoryName(sandboxBundles2Dir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(tempRoot) || !Directory.Exists(tempRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(tempRoot, recursive: true);
            AppLogger.Instance.Info($"已清理沙盒临时目录：{tempRoot}");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"清理沙盒临时目录失败（不影响功能）：{ex.Message}");
        }
    }

    /// <summary>
    /// 从指定 Bundles2 目录创建还原 ZIP（v2 manifest 格式）。
    /// 条目路径统一使用 Bundles2/ 前缀，与参考项目 New-PhysicalRestoreZip 格式一致。
    /// manifest.json 在同一次 Create 中写入，避免 Update 模式的性能问题。
    /// </summary>
    /// <param name="bundles2Dir">源 Bundles2 目录（可以是真实游戏目录或沙盒目录）</param>
    /// <param name="gameDirectory">游戏根目录（用于计算 base_fingerprint）</param>
    /// <param name="outputZip">输出 ZIP 路径</param>
    /// <param name="writePrecondition">写前指纹（null 时自动计算）</param>
    /// <param name="baselineKind">基线类型：byte-exact-prepatch 或 semantic-clean-migration</param>
    /// <param name="installKind">安装类型标识（如 china / international）</param>
    /// <param name="targetPath">目标路径标识（如 BaseItemTypes.datc64 的 bundle 路径）</param>
    /// <param name="preconditionCheckDir">
    /// 写前指纹复核目录。默认 null 时复用 <paramref name="bundles2Dir"/>。
    /// 干净迁移基线场景下：bundles2Dir 是沙盒目录（已被清理层修改），
    /// 但 writePrecondition 是真实游戏 Bundles2 的指纹，此时应传入真实游戏 Bundles2 目录用于复核。
    /// 参考 New-PhysicalRestoreZip 的 Assert-Poe2Bundles2MutationFingerprintCurrent -Bundles2Dir $Bundles2Paths.Bundles2Dir。
    /// </param>
    public void CreateRestoreZipFromCurrentState(
        string bundles2Dir,
        string gameDirectory,
        string outputZip,
        Bundles2Fingerprint? writePrecondition = null,
        string baselineKind = "byte-exact-prepatch",
        string installKind = "",
        string targetPath = "",
        string? preconditionCheckDir = null)
    {
        var outputZipFull = Path.GetFullPath(outputZip);
        var outputDir = Path.GetDirectoryName(outputZipFull);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // 指纹复核目录：默认复用 bundles2Dir；干净迁移场景下传入真实游戏目录
        var fingerprintCheckDir = preconditionCheckDir ?? bundles2Dir;

        // 原子替换：先写入临时文件，校验通过后替换目标
        var tempZip = Path.Combine(
            outputDir ?? ".",
            $".{Path.GetFileName(outputZipFull)}.new-{Guid.NewGuid():N}.tmp");

        try
        {
            // 先计算 base_fingerprint 和 write_precondition（在打包前捕获状态）
            var baseFingerprint = ComputeBaseFingerprint(gameDirectory);
            if (writePrecondition == null)
            {
                // 默认场景：从 bundles2Dir 计算写前指纹（真实游戏目录）
                writePrecondition = ComputeBundles2Fingerprint(fingerprintCheckDir);
            }

            var restoreFiles = new List<FileFingerprint>();
            var rootPrefix = Path.GetFullPath(bundles2Dir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            // 生成 manifest（在创建 ZIP 前准备好，以便一次性写入）
            var manifest = new PhysicalRestoreManifest
            {
                CreatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                InstallKind = installKind,
                TargetPath = targetPath,
                BaselineKind = baselineKind,
                BaseFingerprint = baseFingerprint,
                WritePrecondition = writePrecondition,
                RestoreFiles = restoreFiles, // 引用同一个 list，下面填充
            };

            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                // 打包索引文件（条目路径加 Bundles2/ 前缀）
                foreach (var name in IndexFiles)
                {
                    var src = Path.Combine(bundles2Dir, name);
                    if (File.Exists(src))
                    {
                        var entryName = "Bundles2/" + name;
                        archive.CreateEntryFromFile(src, entryName, CompressionLevel.Optimal);
                        restoreFiles.Add(CreateRestoreFileFingerprint(src, rootPrefix, "Bundles2/"));
                    }
                }

                // 打包 LibGGPK3/ 目录
                var libDir = Path.Combine(bundles2Dir, "LibGGPK3");
                if (Directory.Exists(libDir))
                {
                    foreach (var file in Directory.GetFiles(libDir, "*", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var relative = file.Substring(rootPrefix.Length).Replace('\\', '/');
                        var entryName = "Bundles2/" + relative;
                        archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                        restoreFiles.Add(CreateRestoreFileFingerprint(file, rootPrefix, "Bundles2/"));
                    }
                }

                // 一次性写入 manifest.json（避免 Update 模式重新读取整个 ZIP）
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(
                    manifestEntry.Open(),
                    new UTF8Encoding(false)))
                {
                    var json = JsonSerializer.Serialize(manifest, JsonOptions);
                    writer.Write(json);
                }
            }

            // 校验临时 zip
            ValidateRestoreZip(tempZip, gameDirectory);

            // 写前指纹复核：确认打包期间没有并发修改
            // 干净迁移场景下 writePrecondition 来自真实游戏目录，需对真实游戏目录复核（而非沙盒）
            if (!ValidateFingerprint(writePrecondition, fingerprintCheckDir))
            {
                throw new InvalidOperationException(
                    "还原包创建期间 Bundles2 状态已并发变化，已中止写入");
            }

            // 原子替换目标文件
            AtomicReplaceFile(tempZip, outputZipFull);
            AppLogger.Instance.Info($"已创建还原包（v2 manifest, {baselineKind}）：{outputZipFull}");
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); }
                catch { /* 忽略清理失败 */ }
            }
        }
    }

    /// <summary>
    /// 验证还原 ZIP：检查 manifest、条目完整性、路径白名单和指纹校验。
    /// 参考 Assert-Poe2PhysicalRestoreZip。
    /// 条目路径必须以 Bundles2/ 前缀开头。
    /// </summary>
    public void ValidateRestoreZip(string zipPath, string gameDirectory)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("还原包不存在", zipPath);
        }

        using var archive = ZipFile.OpenRead(zipPath);

        // 读取 manifest
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("还原包缺少 manifest.json");

        PhysicalRestoreManifest manifest;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
        {
            var json = reader.ReadToEnd();
            manifest = JsonSerializer.Deserialize<PhysicalRestoreManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("还原包 manifest.json 解析失败");
        }

        if (manifest.Kind != "poe2-price-patch-physical-restore")
        {
            throw new InvalidDataException($"还原包 manifest kind 无效：{manifest.Kind}");
        }

        if (manifest.Version != 2)
        {
            throw new InvalidDataException($"不支持还原包 manifest version：{manifest.Version}");
        }

        // 条目路径白名单校验 + 重复检测
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowedPattern = new System.Text.RegularExpressions.Regex(
            @"^Bundles2/(?:_\.index\.bin|_\.index\.(?:high|low)\.bin|\.index\.dbg|LibGGPK3/.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var name = entry.FullName;
            if (name != "manifest.json")
            {
                if (!allowedPattern.IsMatch(name))
                {
                    throw new InvalidDataException($"还原包包含不允许的条目：{name}");
                }
            }
            var key = name.ToLowerInvariant();
            if (seenEntries.Contains(key))
            {
                throw new InvalidDataException($"还原包包含重复条目：{name}");
            }
            seenEntries.Add(key);
        }

        // 验证 Bundles2/_.index.bin 存在且非空
        var indexEntry = archive.GetEntry("Bundles2/_.index.bin")
            ?? throw new InvalidDataException("还原包缺少 Bundles2/_.index.bin");
        if (indexEntry.Length <= 1_048_576)
        {
            throw new InvalidDataException($"还原包 Bundles2/_.index.bin 大小异常：{indexEntry.Length} bytes");
        }

        // 验证 base_fingerprint 与当前游戏状态匹配
        if (manifest.BaseFingerprint != null)
        {
            if (manifest.BaseFingerprint.Version != 1 ||
                manifest.BaseFingerprint.Algorithm != "path-length-last-write-time-utc-v1")
            {
                throw new InvalidDataException(
                    $"还原包 base_fingerprint 算法不兼容：v{manifest.BaseFingerprint.Version}/{manifest.BaseFingerprint.Algorithm}");
            }
            var currentBase = ComputeBaseFingerprint(gameDirectory);
            if (manifest.BaseFingerprint.Files.Count != currentBase.Files.Count)
            {
                throw new InvalidDataException(
                    $"还原包已过期：官方底板文件数量已变化（备份 {manifest.BaseFingerprint.Files.Count}，当前 {currentBase.Files.Count}）");
            }
            if (!string.Equals(
                    manifest.BaseFingerprint.InventorySha256,
                    currentBase.InventorySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "还原包已过期：官方底板指纹与当前游戏不一致（游戏可能已更新）");
            }
        }

        // v2：验证 restore_files 与 zip 条目一致
        if (manifest.RestoreFiles.Count > 0)
        {
            var manifestPaths = manifest.RestoreFiles
                .Select(f => f.Path.ToLowerInvariant())
                .ToHashSet();
            var zipPaths = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && e.FullName != "manifest.json")
                .Select(e => e.FullName.Replace('\\', '/').ToLowerInvariant())
                .ToHashSet();

            if (manifestPaths.Count != zipPaths.Count ||
                !manifestPaths.IsSupersetOf(zipPaths))
            {
                throw new InvalidDataException(
                    "还原包 restore_files 与 ZIP 条目不一致");
            }
        }

        AppLogger.Instance.Info($"还原包校验通过：{zipPath}（{archive.Entries.Count} 个条目）");
    }

    /// <summary>
    /// 计算游戏官方底板指纹：PathOfExile*.exe + Bundles2/顶层 *.bundle.bin。
    /// 使用 path-length-last_write_time_utc-v1 算法（不读文件内容，只用大小+修改时间），
    /// 参考 Get-Poe2PhysicalBaseFingerprint。
    /// </summary>
    public Bundles2Fingerprint ComputeBaseFingerprint(string gameDirectory)
    {
        var rootPrefix = Path.GetFullPath(gameDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var files = new List<FileFingerprint>();

        // 游戏根目录的 PathOfExile*.exe
        foreach (var exe in Directory.GetFiles(gameDirectory, "PathOfExile*.exe")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(CreateBaseFileFingerprint(exe, rootPrefix));
        }

        // Bundles2 顶层的 *.bundle.bin
        var bundles2Dir = Path.Combine(gameDirectory, "Bundles2");
        if (Directory.Exists(bundles2Dir))
        {
            foreach (var bundle in Directory.GetFiles(bundles2Dir, "*.bundle.bin")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(CreateBaseFileFingerprint(bundle, rootPrefix));
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "无法计算官方底板指纹：未找到 PathOfExile*.exe 或顶层 *.bundle.bin");
        }

        var sorted = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        // base fingerprint 用 path-length-last_write_time_utc-v1 算法（不读内容，快速检测游戏更新）
        var canonical = string.Join("\n", sorted.Select(f =>
            $"{f.Path.ToLowerInvariant()}|{f.Length}|{f.LastWriteTimeUtc.ToLowerInvariant()}"));

        return new Bundles2Fingerprint
        {
            Version = 1,
            Algorithm = "path-length-last-write-time-utc-v1",
            Files = sorted,
            InventorySha256 = ComputeSha256(canonical),
        };
    }

    /// <summary>
    /// 原子替换文件：使用 File.Replace + 安全备份，失败时自动回滚。
    /// 参考 Move-Poe2FileAtomically。
    /// </summary>
    public static void AtomicReplaceFile(string source, string destination)
    {
        var sourceFull = Path.GetFullPath(source);
        var destinationFull = Path.GetFullPath(destination);
        var destDir = Path.GetDirectoryName(destinationFull);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        // 目标不存在时直接 Move
        if (!File.Exists(destinationFull))
        {
            File.Move(sourceFull, destinationFull);
            return;
        }

        // 目标存在时用 File.Replace 原子替换（支持回滚）
        var safetyBackup = Path.Combine(
            destDir!,
            $".{Path.GetFileName(destinationFull)}.replace-backup-{Guid.NewGuid():N}");
        try
        {
            File.Replace(sourceFull, destinationFull, safetyBackup, ignoreMetadataErrors: true);
        }
        catch
        {
            // 替换失败，尝试恢复安全备份
            if (File.Exists(safetyBackup))
            {
                try
                {
                    if (File.Exists(destinationFull))
                    {
                        File.Replace(safetyBackup, destinationFull, null!, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(safetyBackup, destinationFull);
                    }
                }
                catch
                {
                    // 回滚也失败，保留安全备份供手动恢复
                    throw new IOException(
                        $"原子替换失败，自动回滚也失败。安全备份已保留：{safetyBackup}；目标：{destinationFull}");
                }
            }
            throw;
        }
        finally
        {
            // 成功后清理安全备份
            if (File.Exists(safetyBackup))
            {
                try { File.Delete(safetyBackup); }
                catch { /* 忽略清理失败 */ }
            }
        }
    }

    private static FileFingerprint CreateFileFingerprint(string filePath, string rootPrefix)
    {
        var info = new FileInfo(filePath);
        var relative = filePath.Substring(rootPrefix.Length).Replace('\\', '/');
        return new FileFingerprint
        {
            Path = relative,
            Length = info.Length,
            Sha256 = ComputeFileSha256(filePath),
            LastWriteTimeUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// 创建还原包文件指纹（条目路径带 Bundles2/ 前缀，包含 SHA256）。
    /// </summary>
    private static FileFingerprint CreateRestoreFileFingerprint(string filePath, string rootPrefix, string pathPrefix)
    {
        var info = new FileInfo(filePath);
        var relative = filePath.Substring(rootPrefix.Length).Replace('\\', '/');
        return new FileFingerprint
        {
            Path = pathPrefix + relative,
            Length = info.Length,
            Sha256 = ComputeFileSha256(filePath),
            LastWriteTimeUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
        };
    }

    private static FileFingerprint CreateBaseFileFingerprint(string filePath, string rootPrefix)
    {
        var info = new FileInfo(filePath);
        var relative = filePath.Substring(rootPrefix.Length).Replace('\\', '/');
        return new FileFingerprint
        {
            Path = relative,
            Length = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
        };
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dest = Path.Combine(destination, relative);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            File.Copy(file, dest, overwrite: true);
        }
    }
}
