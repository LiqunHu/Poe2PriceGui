using System.IO;
using System.Text;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 泥人补丁备份存储：移植自 tiny-poe2smoother/src/backup.rs。
/// 自定义二进制格式 2SBK：
/// - 4 字节 magic："2SBK" = 0x4B425332
/// - 4 字节 version = 2
/// - 4 字节 manifest_len + manifest 字符串（含 app 名、版本、游戏目录）
/// - 多个条目：path_len(u32) + path_bytes + data_len(u64) + data_bytes
///
/// 备份策略：只备份 Bundles2/_.index.bin（应用补丁前的当前状态）。
/// 还原策略：覆盖 _.index.bin，并删除 Bundles2/TinyPoe2Smoother/ 目录。
/// </summary>
internal sealed class SmootherBackupStore
{
    private const uint MAGIC = 0x4B425332; // "2SBK"
    private const uint VERSION = 2;
    private const string APP_MANIFEST = "Poe2PriceGui smoother-patch v1.0";

    private readonly string _backupPath;

    public SmootherBackupStore()
    {
        // 备份路径：%LOCALAPPDATA%/Poe2PriceGui/smoother.bak
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(baseDir, "Poe2PriceGui");
        _backupPath = Path.Combine(dir, "smoother.bak");
    }

    /// <summary>
    /// 备份文件的完整路径。
    /// </summary>
    public string BackupPath => _backupPath;

    /// <summary>
    /// 是否存在备份。
    /// </summary>
    public bool HasBackup => File.Exists(_backupPath);

    /// <summary>
    /// 删除备份文件（若不存在则忽略）。
    /// </summary>
    public void Remove()
    {
        if (File.Exists(_backupPath))
        {
            File.Delete(_backupPath);
        }
    }

    /// <summary>
    /// 确保指定相对路径的文件已被备份。
    /// 若备份文件不存在则创建新备份；若某条目已存在则跳过。
    /// 对应 Rust: BackupStore::ensure_originals(game_dir, rel_paths)。
    /// </summary>
    /// <param name="gameDir">游戏目录。</param>
    /// <param name="relPaths">相对路径列表（如 "Bundles2/_.index.bin"）。</param>
    public void EnsureOriginals(string gameDir, IEnumerable<string> relPaths)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);

        if (File.Exists(_backupPath))
        {
            foreach (var entry in ReadEntries())
            {
                known.Add(entry.RelPath);
            }
        }
        else
        {
            // 创建新备份文件（含 magic + version + manifest）
            var dir = Path.GetDirectoryName(_backupPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var manifest = $"{APP_MANIFEST} game={gameDir}";
            var manifestBytes = Encoding.UTF8.GetBytes(manifest);

            using var fs = new FileStream(_backupPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
            bw.Write(MAGIC);
            bw.Write(VERSION);
            bw.Write((uint)manifestBytes.Length);
            bw.Write(manifestBytes);
        }

        // 追加新条目
        using (var fs = new FileStream(_backupPath, FileMode.Append, FileAccess.Write))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
        {
            foreach (var relPath in relPaths)
            {
                if (!known.Add(relPath)) continue; // 已存在
                var abs = Path.Combine(gameDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs))
                {
                    throw new FileNotFoundException($"备份源文件不存在：{abs}");
                }
                var bytes = File.ReadAllBytes(abs);
                var pathBytes = Encoding.UTF8.GetBytes(relPath);
                bw.Write((uint)pathBytes.Length);
                bw.Write(pathBytes);
                bw.Write((ulong)bytes.LongLength);
                bw.Write(bytes);
            }
        }
    }

    /// <summary>
    /// 还原所有备份条目到游戏目录，并删除 TinyPoe2Smoother/ 目录。
    /// 对应 Rust: BackupStore::restore(game_dir)。
    /// </summary>
    /// <returns>还原的文件数。</returns>
    public int Restore(string gameDir)
    {
        var entries = ReadEntries();
        foreach (var entry in entries)
        {
            var abs = Path.Combine(gameDir, entry.RelPath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllBytes(abs, entry.Bytes);
        }

        // 删除 TinyPoe2Smoother/ 目录（自定义 bundle 文件所在）
        var customBundleDir = Path.Combine(gameDir, "Bundles2", "TinyPoe2Smoother");
        if (Directory.Exists(customBundleDir))
        {
            Directory.Delete(customBundleDir, recursive: true);
        }

        // 删除备份文件
        if (File.Exists(_backupPath))
        {
            File.Delete(_backupPath);
        }

        return entries.Count;
    }

    /// <summary>
    /// 读取备份中的所有条目。
    /// </summary>
    public List<SmootherBackupEntry> ReadEntries()
    {
        var result = new List<SmootherBackupEntry>();
        if (!File.Exists(_backupPath)) return result;

        var bytes = File.ReadAllBytes(_backupPath);
        using var ms = new MemoryStream(bytes, false);
        using var br = new BinaryReader(ms);

        if (br.ReadUInt32() != MAGIC)
        {
            throw new InvalidDataException($"不是合法的泥人补丁备份文件：{_backupPath}");
        }
        var version = br.ReadUInt32();
        if (version < 1 || version > VERSION)
        {
            throw new InvalidDataException($"不支持的泥人补丁备份版本 {version}：{_backupPath}");
        }
        if (version >= 2)
        {
            var manifestLen = br.ReadInt32();
            ms.Seek(manifestLen, SeekOrigin.Current);
        }

        while (ms.Position < bytes.Length)
        {
            int pathLen;
            try { pathLen = br.ReadInt32(); }
            catch { break; }
            if (pathLen <= 0 || pathLen > bytes.Length) break;
            var pathBytes = br.ReadBytes(pathLen);
            var dataLen = br.ReadInt64();
            if (dataLen < 0 || dataLen > bytes.Length) break;
            var data = br.ReadBytes((int)dataLen);
            result.Add(new SmootherBackupEntry
            {
                RelPath = Encoding.UTF8.GetString(pathBytes),
                Bytes = data,
            });
        }
        return result;
    }
}

/// <summary>
/// 备份条目：相对路径 + 原始字节。
/// </summary>
internal sealed class SmootherBackupEntry
{
    public string RelPath { get; init; } = "";
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
}
