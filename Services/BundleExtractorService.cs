using LibBundle3;
using LibBundle3.Records;
using BundleIndex = LibBundle3.Index;
using System.IO;
using System.Text;

namespace Poe2PriceGui.Services;

/// <summary>
/// 内嵌的 BundleExtractor 服务：直接调用 LibBundle3 读取 Bundles2 索引并提取文件。
/// 替代原来的 BundleExtractor.exe 进程调用，避免目标机器缺少 .NET 8 运行时的问题。
/// </summary>
public static class BundleExtractorService
{
    /// <summary>
    /// 从游戏 Bundles2 目录提取指定虚拟路径的文件到输出目录。
    /// 输出文件保留虚拟路径的目录结构（与 GGPK 模式一致）。
    /// </summary>
    /// <param name="gameDirectory">游戏根目录，内含 Bundles2/_.index.bin</param>
    /// <param name="virtualPath">bundle 内虚拟路径，如 data/balance/baseitemtypes.datc64</param>
    /// <param name="outputDirectory">提取输出根目录</param>
    /// <returns>提取后的最终文件路径</returns>
    public static string ExtractFile(string gameDirectory, string virtualPath, string outputDirectory)
    {
        var indexBin = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");
        if (!File.Exists(indexBin))
        {
            throw new FileNotFoundException($"未找到索引文件：{indexBin}");
        }

        using var loaded = LoadIndex(indexBin);

        var targetFile = FindFile(loaded.Index, virtualPath)
            ?? throw new InvalidOperationException($"未在 bundle 中找到文件：{virtualPath}");

        var outputPath = Path.Combine(outputDirectory, virtualPath.Replace('/', Path.DirectorySeparatorChar));
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        byte[] data;
        using (var bundle = loaded.Factory.GetBundle(targetFile.BundleRecord))
        {
            data = targetFile.Read(bundle).ToArray();
        }

        File.WriteAllBytes(outputPath, data);
        AppLogger.Instance.Info($"Bundles2 提取成功：{virtualPath} -> {outputPath} ({data.Length} bytes)");
        return outputPath;
    }

    /// <summary>
    /// 列出 _.index.bin 中所有文件，可选按 bundle 前缀过滤。
    /// </summary>
    /// <param name="indexPath">_.index.bin 路径</param>
    /// <param name="outputPath">输出 TSV 路径</param>
    /// <param name="bundlePrefix">可选 bundle 路径前缀过滤，如 LibGGPK3/</param>
    public static void ListFiles(string indexPath, string outputPath, string? bundlePrefix = null)
    {
        using var loaded = LoadIndex(indexPath);

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        writer.WriteLine("path\tbundle\tsize\toffset");
        int written = 0;

        foreach (var file in loaded.Index.Files.Values.OrderBy(f => f.Path ?? ""))
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(bundlePrefix) &&
                !file.BundleRecord.Path.StartsWith(bundlePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            writer.WriteLine(
                $"{CleanTsv(file.Path)}\t{CleanTsv(file.BundleRecord.Path)}\t{file.Size}\t{file.Offset}");
            written++;
        }

        AppLogger.Instance.Info($"Bundles2 列出 {written} 个文件到：{outputPath}");
    }

    private static LoadedIndex LoadIndex(string indexPath)
    {
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException($"Index file not found: {indexPath}", indexPath);
        }

        var bundles2Dir = Path.GetDirectoryName(indexPath) ?? "";
        if (string.IsNullOrEmpty(bundles2Dir))
        {
            bundles2Dir = Environment.CurrentDirectory;
        }

        AppLogger.Instance.Info($"加载 Bundles2 索引：{indexPath}");

        var factory = new DriveBundleFactory(bundles2Dir);
        var index = new BundleIndex(indexPath, false, factory);

        var failedPaths = index.ParsePaths();
        if (failedPaths > 0)
        {
            AppLogger.Instance.Warn($"Bundles2 索引解析：{failedPaths} 个文件路径解析失败（已忽略）");
        }

        AppLogger.Instance.Info($"Bundles2 索引加载完成：{index.Files.Count} 个文件");
        return new LoadedIndex(factory, index);
    }

    private static FileRecord? FindFile(BundleIndex index, string virtualPath)
    {
        foreach (var file in index.Files.Values)
        {
            if (file.Path?.Equals(virtualPath, StringComparison.OrdinalIgnoreCase) == true)
            {
                return file;
            }
        }

        foreach (var file in index.Files.Values)
        {
            if (file.Path?.Contains(virtualPath, StringComparison.OrdinalIgnoreCase) == true)
            {
                AppLogger.Instance.Info($"找到相似文件：{file.Path}");
                return file;
            }
        }

        return null;
    }

    private static string CleanTsv(string value)
    {
        return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private sealed class LoadedIndex : IDisposable
    {
        public LoadedIndex(DriveBundleFactory factory, BundleIndex index)
        {
            Factory = factory;
            Index = index;
        }

        public DriveBundleFactory Factory { get; }

        public BundleIndex Index { get; }

        public void Dispose()
        {
            Index.Dispose();
        }
    }
}
