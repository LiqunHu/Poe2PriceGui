using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Poe2PriceGui.Services.Smoother;
/// <summary>
/// 泥人补丁 GGPK 模式专用工具封装：委托给 SmootherPatchBundledGGPK3 工具完成
/// 备份/还原/应用/健康检查。
/// - apply（应用补丁）：走 LibBundle3.Index.Replace（创建新 bundle + 重指索引 + Save 索引）
/// - restore（还原）：走 LibGGPK3.FileRecord.Write（直接覆盖 _.index.bin 字节，不碰索引对象）
/// - backup（备份）：只读 GGPK 目录树，不写
///
/// 备份策略（解决 141GB Content.ggpk 无法整文件备份的问题）：
/// - 物理上不会复制 Content.ggpk
/// - 调用 --backup 抽出 GGPK 内当前的 Bundles2/_.index.bin（约 119MB）以及
///   （如果已存在）Bundles2/LibGGPK3/*.bundle.bin（Index.Replace 创建的自定义 bundle）
/// - 备份文件 = 一个 zip（与 apply/restore 模式完全兼容）
///
/// 还原策略：
/// - 调用 --restore，通过 FileRecord.Write 直接覆盖 GGPK 内的 _.index.bin 字节
///   （不走 Index.Replace，因为 _.index.bin 是索引文件本身，不是索引内的文件）
/// - GGPK 索引回到备份时状态，补丁的 bundle 数据残留 GGPK 文件尾部（不再被索引，无副作用）
///
/// 应用策略：
/// - 调用 --apply，把补丁 zip（条目名为游戏内虚拟路径）通过 Index.Replace 写入 GGPK
/// - 工具内部为每个文件创建/追加新 bundle，更新索引指向新 bundle，原始 bundle 不动
///
/// 工具路径解析：
/// - 优先 <AppBase>/tools/SmootherPatchBundledGGPK3/SmootherPatchBundledGGPK3.exe
/// - 其次 <AppBase>/tools/SmootherPatchBundledGGPK3/bin/Release/net8.0/SmootherPatchBundledGGPK3.exe
/// - 最后 <Src>/tools/SmootherPatchBundledGGPK3/bin/Release/net8.0/SmootherPatchBundledGGPK3.exe
///
/// 任何一种找不到都抛 FileNotFoundException，让上层（SmootherPatchService）
/// 暴露给 UI 显示具体错误。
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
    /// 工具已自动做幂等（zip 总是覆盖创建）；如备份文件已存在，会被替换。
    /// </summary>
    public void Backup()
    {
        // 1. 先做 GGPK 健康检查（probe），GGPK 打不开就立刻报错，避免后面失败时
        //    旧备份被覆盖导致无法恢复。
        var probeExit = RunTool("--probe", _ggpkPath, expectedExit: 0);
        if (probeExit != 0)
        {
            throw new InvalidOperationException(
                $"GGPK 健康检查失败（exit={probeExit}），拒绝覆盖现有备份：{_backupZipPath}");
        }

        // 2. 调用 --backup 把当前 GGPK 内的 index 抽出到 zip。
        var backupExit = RunTool("--backup", _ggpkPath, _backupZipPath, expectedExit: 0);
        if (backupExit != 0)
        {
            throw new InvalidOperationException(
                $"GGPK 备份失败（exit={backupExit}）：{_backupZipPath}");
        }
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
        var exit = RunTool("--restore", _ggpkPath, _backupZipPath, expectedExit: 0);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"GGPK 还原失败（exit={exit}）：{_backupZipPath}");
        }
        // 还原成功后删除备份文件（与 Bundles2 模式保持一致：成功还原后清掉备份）
        Remove();
    }

    /// <summary>
    /// 应用补丁：把补丁 zip（条目名为游戏内虚拟路径，如 "metadata/effects/.../.epk"）
    /// 通过 SmootherPatchBundledGGPK3 --apply 写入 Content.ggpk。
    ///
    /// 工具内部调用 LibBundle3.Index.Replace，为每个文件创建/追加新 bundle
    /// （Bundles2/LibGGPK3/*.bundle.bin）并更新索引指向新 bundle，原始 bundle 不动。
    /// 这与 poe2_price-main 的 PatchBundledGGPK3 写入方式一致，是 GGPK 模式下
    /// 安全且高效的做法（不原地改 100GB+ 文件中的大 bundle）。
    ///
    /// 调用前必须确保本进程已关闭对 Content.ggpk 的所有句柄（工具需独占写入）。
    /// </summary>
    /// <param name="patchZipPath">补丁 zip 路径，条目名为虚拟路径，条目内容为文件字节。</param>
    public void ApplyPatch(string patchZipPath)
    {
        if (!File.Exists(patchZipPath))
        {
            throw new FileNotFoundException($"补丁 zip 不存在：{patchZipPath}", patchZipPath);
        }
        var exit = RunTool("--apply", _ggpkPath, patchZipPath, expectedExit: 0);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"GGPK 应用补丁失败（exit={exit}）：{patchZipPath}");
        }
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

    // --- 工具调用 ---

    private int RunTool(string mode, string arg1, string? arg2 = null, int expectedExit = 0)
    {
        var toolPath = ResolveToolPath();
        if (string.IsNullOrEmpty(toolPath) || !File.Exists(toolPath))
        {
            throw new FileNotFoundException(
                "找不到 SmootherPatchBundledGGPK3.exe，请确认 tools/SmootherPatchBundledGGPK3 已正确编译并随 Release 打包。");
        }

        // 注意：使用 ProcessStartInfo.ArgumentList.Add() 时，.NET 运行时会自动给每个参数
        // 加引号。如果手动再用 Quote() 包一层引号，就会变成 ""C:\..." 这种双引号字符串，
        // 工具进程收到的路径会带字面量引号字符，导致 File.Exists 永远返回 false（exit=2）。
        // 这里直接传原字符串，让运行时处理转义。
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(arg1);
        if (arg2 != null) psi.ArgumentList.Add(arg2);

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("无法启动 SmootherPatchBundledGGPK3 进程");
        }
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            AppLogger.Instance.Info($"[SmootherPatchBundledGGPK3 {mode}] {stdout.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            AppLogger.Instance.Warn($"[SmootherPatchBundledGGPK3 {mode} stderr] {stderr.Trim()}");
        }
        return process.ExitCode;
    }

    /// <summary>
    /// 解析 SmootherPatchBundledGGPK3.exe 的位置，按以下顺序查找：
    /// 1. <AppBase>/tools/SmootherPatchBundledGGPK3/SmootherPatchBundledGGPK3.exe（发布结构）
    /// 2. <Src>/tools/SmootherPatchBundledGGPK3/SmootherPatchBundledGGPK3.exe（开发环境）
    ///    源码在 test_tools/，OutputPath 指向 ../../tools/，所以产物仍落在 tools/。
    /// 注意：只返回 .exe，.dll 不能作为 Process.Start 的 FileName 执行。
    /// </summary>
    private static string ResolveToolPath()
    {
        var candidates = new List<string>
        {
            // 1) 主项目发布结构：test_tools 子项目 OutputPath 直接落 tools/，扁平 exe+dll
            Path.Combine(AppContext.BaseDirectory, "tools", "SmootherPatchBundledGGPK3", "SmootherPatchBundledGGPK3.exe"),
            // 2) 源码目录的 build 输出（开发环境用）
            // 源码在 test_tools/，但 OutputPath=../../tools/，所以产物仍落在 tools/。
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "SmootherPatchBundledGGPK3", "SmootherPatchBundledGGPK3.exe"),
        };
        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // 忽略路径解析错误，继续尝试下一个
            }
        }
        return "";
    }
}
