using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// Bundles2 读取层：移植自 tiny-poe2smoother/src/bundle.rs 的读取部分。
/// 负责 _.index.bin 解析、bundle 解压、路径重建、按路径查找文件。
/// 同时提供写回 API：CreateCustomBundle + ApplyBundleReplacements，
/// 直接修改 _.index.bin 并生成 TinyPoe2Smoother/{ordinal}.bundle.bin。
/// </summary>
public sealed class BundleStore
{
    /// <summary>
    /// bundle 单个 chunk 的最大未压缩大小：256 KiB。
    /// 对应 Rust: BUNDLE_CHUNK_SIZE = 0x40000。
    /// </summary>
    private const int BUNDLE_CHUNK_SIZE = 0x40000;

    /// <summary>
    /// bundle 头部固定部分大小（在 chunk_sizes 数组之前）。
    /// encoding(4) + unknown(4) + uncompressed_size(8) + compressed_size(8) +
    /// chunk_count(4) + chunk_unpacked_size(4) + 4×padding(16) = 48。
    /// 对应 Rust: BUNDLE_FIXED_HEAD_SIZE_AFTER_PREFIX = 48。
    /// </summary>
    private const int BUNDLE_FIXED_HEAD_SIZE_AFTER_PREFIX = 48;

    private readonly string _gameDir;
    private readonly string _bundlesDir;
    private readonly string _indexPath;

    /// <summary>
    /// oo2core.dll 的搜索路径（与 BundleExtractor.exe / PatchBundle3.exe 共用同一份）。
    /// P/Invoke 会按 PATH 顺序查找，我们显式预加载以确保使用正确的 DLL。
    /// </summary>
    private readonly string _oodleDllPath;

    public BundleStore(string gameDir)
    {
        _gameDir = gameDir;
        _bundlesDir = Path.Combine(gameDir, "Bundles2");
        _indexPath = Path.Combine(_bundlesDir, "_.index.bin");
        _oodleDllPath = Path.Combine(AppContext.BaseDirectory, "tools", "BundleExtractor", "oo2core.dll");
    }

    public string GameDirectory => _gameDir;
    public string BundlesDirectory => _bundlesDir;
    public string IndexPath => _indexPath;

    /// <summary>
    /// 打开并解析 _.index.bin。
    /// _.index.bin 本身是一个 Oodle 压缩的 bundle，解压后是 index 数据。
    /// </summary>
    public BundleIndex OpenIndex()
    {
        EnsureOodleLoaded();
        var bytes = File.ReadAllBytes(_indexPath);
        var decompressed = DecompressBundle(bytes);
        return BundleIndex.Parse(decompressed);
    }

    /// <summary>
    /// 读取指定路径的文件内容。
    /// </summary>
    public byte[] ReadFile(BundleIndex index, string path)
    {
        var file = index.FileByPath(path);
        if (file == null)
        {
            throw new FileNotFoundException($"路径不在 bundle index 中：{path}");
        }
        var bundle = ReadBundle(file.BundleName);
        return SliceFile(bundle, file);
    }

    /// <summary>
    /// 读取并解压一个 bundle。
    /// </summary>
    public byte[] ReadBundle(string bundleName)
    {
        var path = BundlePath(bundleName);
        var bytes = File.ReadAllBytes(path);
        return DecompressBundle(bytes);
    }

    /// <summary>
    /// 批量读取多个 bundle，返回 bundle 名 → 解压后字节的映射。
    /// </summary>
    public Dictionary<string, byte[]> ReadBundlesBatch(IEnumerable<string> names)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            result[name] = ReadBundle(name);
        }
        return result;
    }

    /// <summary>
    /// bundle 文件在磁盘上的完整路径。
    /// </summary>
    public string BundlePath(string bundleName)
    {
        return Path.Combine(_bundlesDir, $"{bundleName}.bundle.bin");
    }

    /// <summary>
    /// 从解压后的 bundle 字节中切出指定文件的字节。
    /// 对应 Rust: slice_file(bundle, file)。
    /// </summary>
    public static byte[] SliceFile(byte[] bundle, BundleFile file)
    {
        var start = (int)file.Offset;
        var end = start + (int)file.Size;
        if (end > bundle.Length)
        {
            throw new InvalidOperationException($"文件切片超出 bundle 长度：{file.BundleName}");
        }
        var result = new byte[file.Size];
        Array.Copy(bundle, start, result, 0, file.Size);
        return result;
    }

    /// <summary>
    /// 解压一个 Oodle 压缩的 bundle。
    /// 对应 Rust: decompress_bundle(src)。
    /// </summary>
    /// <remarks>
    /// bundle 二进制布局：
    /// - 前 12 字节 prefix：total_uncompressed(u32)、total_compressed(u32)、head_size(u32)
    /// - head_size 字节头部：
    ///   encoding(u32)、unknown(u32)、uncompressed_size(u64)、compressed_size(u64)、
    ///   chunk_count(u32)、chunk_unpacked_size(u32)、4×u32 padding、chunk_sizes[chunk_count](u32)
    /// - 压缩数据：chunk_count 个 Oodle 压缩块
    /// </summary>
    public static byte[] DecompressBundle(byte[] src)
    {
        using var ms = new MemoryStream(src, false);
        using var br = new BinaryReader(ms);

        var totalUncompressed32 = br.ReadUInt32();
        var totalCompressed32 = br.ReadUInt32();
        var headSize = br.ReadUInt32();

        var encoding = br.ReadUInt32();
        var unknown = br.ReadUInt32();
        var uncompressedSize = br.ReadUInt64();
        var compressedSize = br.ReadUInt64();
        var chunkCount = br.ReadUInt32();
        var chunkUnpackedSize = br.ReadUInt32();
        // 4×u32 padding
        for (var i = 0; i < 4; i++)
        {
            br.ReadUInt32();
        }

        var chunkSizes = new int[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            chunkSizes[i] = (int)br.ReadUInt32();
        }

        var offset = 12 + (int)headSize;
        var cursorPos = (int)ms.Position;
        if (offset < cursorPos)
        {
            throw new InvalidDataException("bundle head_size 小于 chunk table");
        }

        var outBuf = new byte[uncompressedSize];
        var remaining = (int)uncompressedSize;
        var outPos = 0;
        foreach (var size in chunkSizes)
        {
            if (offset + size > src.Length)
            {
                throw new InvalidDataException("bundle chunk 超出源数据长度");
            }
            var dstSize = Math.Min(remaining, (int)chunkUnpackedSize);
            var chunkOut = new byte[dstSize + 64];
            // Oodle P/Invoke 从缓冲区起始读取，需把 chunk 切片到独立数组。
            var chunkSrc = new byte[size];
            Array.Copy(src, offset, chunkSrc, 0, size);
            var wrote = OodleNative.Decompress(chunkSrc, size, chunkOut, dstSize);
            if (wrote < 0)
            {
                throw new InvalidDataException($"Oodle 解压失败，返回码 {wrote}");
            }
            Array.Copy(chunkOut, 0, outBuf, outPos, dstSize);
            outPos += dstSize;
            remaining -= dstSize;
            offset += size;
        }

        if (outPos != (int)uncompressedSize)
        {
            throw new InvalidDataException($"bundle 解压为 {outPos} 字节，预期 {(int)uncompressedSize} 字节");
        }
        return outBuf;
    }

    /// <summary>
    /// 预加载 oo2core.dll。
    /// P/Invoke 的 DllImport("oo2core.dll") 默认从工作目录和 PATH 搜索，
    /// 但 oo2core.dll 位于 tools/BundleExtractor/ 子目录，所以需要用
    /// NativeLibrary.Load 显式按完整路径加载，让进程持有该 DLL，
    /// 后续 DllImport 调用即可命中已加载的模块。
    /// </summary>
    private void EnsureOodleLoaded()
    {
        if (!File.Exists(_oodleDllPath))
        {
            throw new FileNotFoundException($"找不到 oo2core.dll：{_oodleDllPath}");
        }
        if (_oodleLoaded) return;
        var handle = NativeLibrary.Load(_oodleDllPath);
        if (handle == IntPtr.Zero)
        {
            throw new DllNotFoundException($"NativeLibrary.Load 失败：{_oodleDllPath}");
        }
        // 句柄不需要释放：进程退出时自动卸载。
        // 让 DllImport("oo2core.dll") 后续调用命中已加载模块。
        _oodleLoaded = true;
    }

    private bool _oodleLoaded;

    #region 写回 API（直接修改 _.index.bin + 生成 TinyPoe2Smoother/ bundle）

    /// <summary>
    /// 将变换后的文件内容写入 Bundles2：
    /// 1. 在 index 中创建 TinyPoe2Smoother/{ordinal} 自定义 bundle 记录；
    /// 2. 将所有替换内容追加到 custom_data，更新各文件记录指向新 bundle；
    /// 3. 更新自定义 bundle 的 uncompressed_size；
    /// 4. 用 Oodle 压缩 custom_data，写入 Bundles2/TinyPoe2Smoother/{ordinal}.bundle.bin；
    /// 5. 把修改后的 index 重新打包写回 _.index.bin。
    ///
    /// 对应 Rust: apply_bundle_replacements(store, index, replacements)。
    /// </summary>
    /// <param name="index">已解析的 BundleIndex（会被原地修改）。</param>
    /// <param name="replacements">bundle 名 → (文件记录, 变换后字节) 列表。</param>
    /// <returns>被修改的文件路径列表（自定义 bundle 文件 + index 文件）。</returns>
    public List<string> ApplyBundleReplacements(
        BundleIndex index,
        Dictionary<string, List<(BundleFile File, byte[] Data)>> replacements)
    {
        EnsureOodleLoaded();

        var touched = new List<string>();
        var generatedBundlePaths = new List<string>();

        // 1. 创建自定义 bundle 记录，得到其 bundle_index
        var customBundleIndex = index.CreateCustomBundle();
        var customBundleName = index.GetBundleName(customBundleIndex);
        var customData = new MemoryStream();

        // 2. 收集所有替换项并按 file_order 排序（保证写入顺序与原始 index 一致）
        var edits = new List<(BundleFile File, byte[] Data)>();
        foreach (var items in replacements.Values)
        {
            edits.AddRange(items);
        }
        var fileOrder = index.FileOrderMap();
        edits.Sort((a, b) =>
        {
            var ia = fileOrder.TryGetValue(a.File.Hash, out var oa) ? oa : int.MaxValue;
            var ib = fileOrder.TryGetValue(b.File.Hash, out var ob) ? ob : int.MaxValue;
            return ia.CompareTo(ib);
        });

        // 3. 逐个追加替换内容，更新文件记录
        foreach (var (file, replacement) in edits)
        {
            var newOffset = (uint)customData.Length;
            customData.Write(replacement, 0, replacement.Length);
            index.UpdateFileRecord(file.Hash, customBundleIndex, newOffset, (uint)replacement.Length);
        }
        index.UpdateBundleSize(customBundleIndex, (uint)customData.Length);

        // 4. 压缩 custom_data 并写入自定义 bundle 文件
        var customBytes = customData.ToArray();
        var packedBundle = PackUncompressedBundle(customBytes);
        var customPath = BundlePath(customBundleName);
        var customDir = Path.GetDirectoryName(customPath);
        if (!string.IsNullOrEmpty(customDir))
        {
            Directory.CreateDirectory(customDir);
        }

        try
        {
            AtomicWrite(customPath, packedBundle);
        }
        catch (Exception ex)
        {
            // 清理已生成的 bundle
            foreach (var p in generatedBundlePaths)
            {
                try { File.Delete(p); } catch { /* ignore */ }
            }
            throw new InvalidOperationException($"写入自定义 bundle 失败：{customPath}；{ex.Message}", ex);
        }
        generatedBundlePaths.Add(customPath);
        touched.Add(customPath);

        // 5. 把修改后的 index 重新打包写回 _.index.bin
        var indexBytes = index.PackedBytes();
        try
        {
            AtomicWrite(_indexPath, indexBytes);
        }
        catch (Exception ex)
        {
            // index 写入失败，删除已生成的自定义 bundle 避免悬空文件
            foreach (var p in generatedBundlePaths)
            {
                try { File.Delete(p); } catch { /* ignore */ }
            }
            throw new InvalidOperationException($"写入 _.index.bin 失败；已清理生成的 bundle。{ex.Message}", ex);
        }
        touched.Add(_indexPath);

        return touched;
    }

    /// <summary>
    /// 原子写入：先写到 .tmp 临时文件，再替换目标文件。
    /// 对应 Rust: atomic_write(path, bytes)。
    /// </summary>
    public static void AtomicWrite(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmpPath = path + ".tmp";
        File.WriteAllBytes(tmpPath, bytes);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(tmpPath, path);
    }

    /// <summary>
    /// 把未压缩数据打包成 Oodle 压缩的 bundle 字节流（与游戏 bundle.bin 同格式）。
    /// 对应 Rust: pack_uncompressed_bundle(data)。
    ///
    /// 布局：
    /// - prefix(12)：total_uncompressed(u32)、total_compressed(u32)、head_size(u32)
    /// - head(48 + 4×chunk_count)：
    ///   encoding(u32)=9、unknown(u32)=1、uncompressed_size(u64)、compressed_size(u64)、
    ///   chunk_count(u32)、chunk_unpacked_size(u32)=0x40000、4×padding(u32)=0、chunk_sizes[chunk_count](u32)
    /// - 压缩数据：chunk_count 个 Oodle (Mermaid) 压缩块
    /// </summary>
    public static byte[] PackUncompressedBundle(byte[] data)
    {
        // 切分为 256KiB 的 chunk，逐个压缩
        var chunkCount = (data.Length + BUNDLE_CHUNK_SIZE - 1) / BUNDLE_CHUNK_SIZE;
        if (chunkCount == 0) chunkCount = 1; // 空数据也至少 1 个 chunk

        var chunks = new List<byte[]>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var start = i * BUNDLE_CHUNK_SIZE;
            var len = Math.Min(BUNDLE_CHUNK_SIZE, data.Length - start);
            var chunk = new byte[len];
            Array.Copy(data, start, chunk, 0, len);
            chunks.Add(OodleNative.CompressChunk(chunk));
        }

        var compressedLen = 0;
        foreach (var c in chunks) compressedLen += c.Length;

        var headSize = BUNDLE_FIXED_HEAD_SIZE_AFTER_PREFIX + 4 * chunks.Count;
        var totalFileSize = 12 + headSize + compressedLen;

        using var ms = new MemoryStream(totalFileSize);
        using var bw = new BinaryWriter(ms);

        // prefix
        bw.Write((uint)data.Length);               // total_uncompressed
        bw.Write((uint)compressedLen);              // total_compressed
        bw.Write((uint)headSize);                  // head_size

        // head
        bw.Write((uint)OodleNative.OODLE_MERMAID_COMPRESSOR); // encoding = 9
        bw.Write((uint)1);                          // unknown = 1
        bw.Write((ulong)data.Length);              // uncompressed_size
        bw.Write((ulong)compressedLen);             // compressed_size
        bw.Write((uint)chunks.Count);              // chunk_count
        bw.Write((uint)BUNDLE_CHUNK_SIZE);         // chunk_unpacked_size
        for (var i = 0; i < 4; i++) bw.Write((uint)0); // 4×padding

        // chunk_sizes
        foreach (var c in chunks)
        {
            bw.Write((uint)c.Length);
        }

        // 压缩数据
        foreach (var c in chunks)
        {
            bw.Write(c);
        }

        return ms.ToArray();
    }

    #endregion
}

/// <summary>
/// Bundles2 的 index 解析结果。
/// 对应 Rust: BundleIndex。
/// </summary>
public sealed class BundleIndex
{
    private byte[] _rawDecompressed;
    private List<BundleInfo> _bundles;
    private HashMode _hashMode;
    private Dictionary<ulong, BundleFile> _files;
    private List<ulong> _fileOrder;
    private int _fileCountPos;
    private byte[] _directoryBytesCompressed;
    private List<DirectoryRecord> _directories;
    private List<string>? _paths;

    private BundleIndex(
        byte[] rawDecompressed,
        List<BundleInfo> bundles,
        HashMode hashMode,
        Dictionary<ulong, BundleFile> files,
        List<ulong> fileOrder,
        int fileCountPos,
        byte[] directoryBytesCompressed,
        List<DirectoryRecord> directories)
    {
        _rawDecompressed = rawDecompressed;
        _bundles = bundles;
        _hashMode = hashMode;
        _files = files;
        _fileOrder = fileOrder;
        _fileCountPos = fileCountPos;
        _directoryBytesCompressed = directoryBytesCompressed;
        _directories = directories;
        _paths = null;
    }

    /// <summary>
    /// 检测 index 中是否存在以指定前缀开头的 bundle。
    /// 用于判断是否已应用过泥人补丁（TinyPoe2Smoother/）或物价补丁（LibGGPK3/）。
    /// </summary>
    public bool HasBundlePrefix(string prefix)
    {
        foreach (var b in _bundles)
        {
            if (b.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 统计指向指定前缀 bundle 的文件数量。
    /// 用于检测泥人补丁实际修改了多少个文件。
    /// </summary>
    public int CountFilesByBundlePrefix(string prefix)
    {
        var matchingBundleIndices = new HashSet<uint>();
        for (var i = 0; i < _bundles.Count; i++)
        {
            if (_bundles[i].Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                matchingBundleIndices.Add((uint)i);
            }
        }
        if (matchingBundleIndices.Count == 0) return 0;
        var count = 0;
        foreach (var file in _files.Values)
        {
            if (matchingBundleIndices.Contains(file.BundleIndex))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 返回所有以指定前缀开头的 bundle 名称列表。
    /// </summary>
    public List<string> GetBundleNamesByPrefix(string prefix)
    {
        var result = new List<string>();
        foreach (var b in _bundles)
        {
            if (b.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Add(b.Name);
            }
        }
        return result;
    }

    /// <summary>
    /// 按路径查找文件记录。
    /// 对应 Rust: file_by_path(path)。
    /// </summary>
    public BundleFile? FileByPath(string path)
    {
        var hash = HashPath(_hashMode, path);
        return _files.TryGetValue(hash, out var file) ? file : null;
    }

    /// <summary>
    /// 确保路径列表已构建。
    /// 对应 Rust: ensure_paths_built()。
    /// </summary>
    public List<string> EnsurePathsBuilt()
    {
        if (_paths != null)
        {
            return _paths;
        }
        var directoryBytes = BundleStore.DecompressBundle(_directoryBytesCompressed);
        _paths = BuildPathsFromDirectories(directoryBytes, _directories);
        return _paths;
    }

    /// <summary>
    /// 当前已构建的路径列表（可能为空）。
    /// </summary>
    public IReadOnlyList<string> Paths => _paths ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// 按前缀 + 扩展名匹配文件。
    /// 对应 Rust: matching_paths(prefix, extensions)。
    /// </summary>
    /// <param name="prefix">小写路径前缀，使用 '/' 分隔。</param>
    /// <param name="extensions">小写扩展名列表（含点号，如 ".pet"）。</param>
    public List<IndexedPath> MatchingPaths(string prefix, string[] extensions)
    {
        return MatchingPathsBy(path =>
        {
            var normalized = path.Replace('\\', '/').ToLowerInvariant();
            return normalized.StartsWith(prefix, StringComparison.Ordinal)
                && Array.Exists(extensions, ext => normalized.EndsWith(ext, StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// 按谓词匹配文件。
    /// 对应 Rust: matching_paths_by(predicate)。
    /// </summary>
    public List<IndexedPath> MatchingPathsBy(Func<string, bool> predicate)
    {
        var pathsList = EnsurePathsBuilt();
        var result = new List<IndexedPath>();
        foreach (var path in pathsList)
        {
            if (!predicate(path))
            {
                continue;
            }
            var hash = HashPath(_hashMode, path);
            if (_files.TryGetValue(hash, out var file))
            {
                result.Add(new IndexedPath { Path = path, File = file });
            }
        }
        return result;
    }

    /// <summary>
    /// 获取所有带路径的索引条目。
    /// 对应 Rust: indexed_paths()。
    /// </summary>
    public List<IndexedPath> IndexedPaths()
    {
        var pathsList = EnsurePathsBuilt();
        var result = new List<IndexedPath>(pathsList.Count);
        foreach (var path in pathsList)
        {
            var hash = HashPath(_hashMode, path);
            if (_files.TryGetValue(hash, out var file))
            {
                result.Add(new IndexedPath { Path = path, File = file });
            }
        }
        return result;
    }

    #region 写回 API

    /// <summary>
    /// 创建一个新的 TinyPoe2Smoother/{ordinal} 自定义 bundle 记录。
    /// 在 _rawDecompressed 的 _fileCountPos 位置插入 bundle 记录，
    /// 然后调整 _fileCountPos 和所有文件记录的 RecordPos。
    /// 对应 Rust: BundleIndex::create_custom_bundle()。
    /// </summary>
    /// <returns>新 bundle 在 _bundles 列表中的索引。</returns>
    public uint CreateCustomBundle()
    {
        var ordinal = 0;
        while (true)
        {
            var name = $"TinyPoe2Smoother/{ordinal}";
            var exists = false;
            foreach (var b in _bundles)
            {
                if (b.Name == name)
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                var bundleIndex = (uint)_bundles.Count;

                // 构造 bundle 记录：name_len(u32) + name_bytes + uncompressed_size(u32)=0
                var nameBytes = Encoding.UTF8.GetBytes(name);
                var record = new byte[4 + nameBytes.Length + 4];
                using var ms = new MemoryStream(record, true);
                using var bw = new BinaryWriter(ms);
                bw.Write((uint)nameBytes.Length);
                bw.Write(nameBytes);
                var sizePos = _fileCountPos + (int)ms.Position;
                bw.Write((uint)0); // uncompressed_size = 0（稍后由 UpdateBundleSize 设置）

                // 在 _fileCountPos 位置插入 record
                var newRaw = new byte[_rawDecompressed.Length + record.Length];
                Array.Copy(_rawDecompressed, 0, newRaw, 0, _fileCountPos);
                Array.Copy(record, 0, newRaw, _fileCountPos, record.Length);
                Array.Copy(_rawDecompressed, _fileCountPos, newRaw, _fileCountPos + record.Length, _rawDecompressed.Length - _fileCountPos);
                _rawDecompressed = newRaw;

                var delta = record.Length;
                _fileCountPos += delta;
                // 文件记录位置向后偏移 delta（它们都在 _fileCountPos 之后）
                foreach (var file in _files.Values)
                {
                    file.RecordPos += delta;
                }

                _bundles.Add(new BundleInfo
                {
                    Name = name,
                    UncompressedSize = 0,
                    SizePos = sizePos,
                });

                // 更新 _rawDecompressed 头部 4 字节的 bundle 数量
                Array.Copy(BitConverter.GetBytes((uint)_bundles.Count), 0, _rawDecompressed, 0, 4);

                return bundleIndex;
            }
            ordinal++;
        }
    }

    /// <summary>
    /// 更新文件记录：将其指向新的 bundle/offset/size。
    /// 同时更新 _rawDecompressed 中对应位置的 12 字节（bundle_index + offset + size）。
    /// 对应 Rust: BundleIndex::update_file_record(hash, bundle_index, offset, size)。
    /// </summary>
    public void UpdateFileRecord(ulong hash, uint bundleIndex, uint offset, uint size)
    {
        if (!_files.TryGetValue(hash, out var file))
        {
            throw new KeyNotFoundException($"文件哈希不在 index 中：0x{hash:X}");
        }
        if (bundleIndex >= _bundles.Count)
        {
            throw new IndexOutOfRangeException($"无效的 bundle 索引 {bundleIndex}");
        }
        var bundleName = _bundles[(int)bundleIndex].Name;

        // 更新 BundleFile 对象
        file.BundleIndex = bundleIndex;
        file.Offset = offset;
        file.Size = size;
        file.BundleName = bundleName;

        // 更新 _rawDecompressed 中的 12 字节记录（位于 RecordPos + 8 处）
        var pos = file.RecordPos + 8;
        var src = BitConverter.GetBytes(bundleIndex);
        Array.Copy(src, 0, _rawDecompressed, pos, 4);
        Array.Copy(BitConverter.GetBytes(offset), 0, _rawDecompressed, pos + 4, 4);
        Array.Copy(BitConverter.GetBytes(size), 0, _rawDecompressed, pos + 8, 4);
    }

    /// <summary>
    /// 更新 bundle 的 uncompressed_size 字段。
    /// 同时更新 _rawDecompressed 中对应位置（bundle.SizePos）的 4 字节。
    /// 对应 Rust: BundleIndex::update_bundle_size(bundle_index, uncompressed_size)。
    /// </summary>
    public void UpdateBundleSize(uint bundleIndex, uint uncompressedSize)
    {
        if (bundleIndex >= _bundles.Count)
        {
            throw new IndexOutOfRangeException($"无效的 bundle 索引 {bundleIndex}");
        }
        var bundle = _bundles[(int)bundleIndex];
        bundle.UncompressedSize = uncompressedSize;
        Array.Copy(BitConverter.GetBytes(uncompressedSize), 0, _rawDecompressed, bundle.SizePos, 4);
    }

    /// <summary>
    /// 获取 hash → 在 _fileOrder 中的位置映射。
    /// 用于按原始 index 顺序排序文件编辑。
    /// 对应 Rust: BundleIndex::file_order_map()。
    /// </summary>
    public Dictionary<ulong, int> FileOrderMap()
    {
        var map = new Dictionary<ulong, int>(_fileOrder.Count);
        for (var i = 0; i < _fileOrder.Count; i++)
        {
            map[_fileOrder[i]] = i;
        }
        return map;
    }

    /// <summary>
    /// 获取指定索引的 bundle 名称。
    /// </summary>
    public string GetBundleName(uint bundleIndex)
    {
        if (bundleIndex >= _bundles.Count)
        {
            throw new IndexOutOfRangeException($"无效的 bundle 索引 {bundleIndex}");
        }
        return _bundles[(int)bundleIndex].Name;
    }

    /// <summary>
    /// 将 _rawDecompressed（已修改的 index 字节）重新打包为 Oodle 压缩的 bundle 字节流，
    /// 用于写回 _.index.bin。
    /// 对应 Rust: BundleIndex::packed_bytes()。
    /// </summary>
    public byte[] PackedBytes()
    {
        return BundleStore.PackUncompressedBundle(_rawDecompressed);
    }

    #endregion

    /// <summary>
    /// 解析 _.index.bin 的解压后字节。
    /// 对应 Rust: BundleIndex::parse(raw_decompressed)。
    /// </summary>
    public static BundleIndex Parse(byte[] rawDecompressed)
    {
        using var ms = new MemoryStream(rawDecompressed, false);
        using var br = new BinaryReader(ms);

        var bundleCount = br.ReadUInt32();
        var bundles = new List<BundleInfo>((int)bundleCount);

        for (var i = 0; i < bundleCount; i++)
        {
            var len = br.ReadUInt32();
            var nameBytes = br.ReadBytes((int)len);
            var sizePos = (int)ms.Position;
            var uncompressedSize = br.ReadUInt32();
            bundles.Add(new BundleInfo
            {
                Name = Encoding.UTF8.GetString(nameBytes),
                UncompressedSize = uncompressedSize,
                SizePos = sizePos,
            });
        }

        var fileCountPos = (int)ms.Position;
        var fileCount = br.ReadUInt32();
        var files = new Dictionary<ulong, BundleFile>((int)fileCount);
        var fileOrder = new List<ulong>((int)fileCount);
        for (var i = 0; i < fileCount; i++)
        {
            var recordPos = (int)ms.Position;
            var hash = br.ReadUInt64();
            var bundleIndex = br.ReadUInt32();
            var offset = br.ReadUInt32();
            var size = br.ReadUInt32();
            if (bundleIndex >= bundles.Count)
            {
                throw new InvalidDataException($"文件引用了无效的 bundle 索引 {bundleIndex}");
            }
            var bundleName = bundles[(int)bundleIndex].Name;
            fileOrder.Add(hash);
            files[hash] = new BundleFile
            {
                Hash = hash,
                BundleIndex = bundleIndex,
                BundleName = bundleName,
                Offset = offset,
                Size = size,
                RecordPos = recordPos,
            };
        }

        var directoryCount = br.ReadUInt32();
        var directories = new List<DirectoryRecord>((int)directoryCount);
        for (var i = 0; i < directoryCount; i++)
        {
            directories.Add(new DirectoryRecord
            {
                PathHash = br.ReadUInt64(),
                Offset = br.ReadUInt32(),
                Size = br.ReadUInt32(),
                RecursiveSize = br.ReadUInt32(),
            });
        }

        var directoryBytesCompressed = new byte[rawDecompressed.Length - ms.Position];
        Array.Copy(rawDecompressed, (int)ms.Position, directoryBytesCompressed, 0, directoryBytesCompressed.Length);

        var hashMode = directories.Count > 0
            ? directories[0].PathHash switch
            {
                0xF42A94E69CFF42FE => HashMode.Murmur64A,
                0x07E47507B4A92E53 => HashMode.Fnv1A,
                var v => throw new InvalidDataException($"不支持的 index 哈希哨兵值 0x{v:X}"),
            }
            : throw new InvalidDataException("index 不包含任何目录记录");

        return new BundleIndex(
            rawDecompressed,
            bundles,
            hashMode,
            files,
            fileOrder,
            fileCountPos,
            directoryBytesCompressed,
            directories);
    }

    /// <summary>
    /// 从解压后的 directory 字节重建路径列表。
    /// 对应 Rust: build_paths_from_directories(bytes, directories)。
    /// </summary>
    private static List<string> BuildPathsFromDirectories(byte[] bytes, List<DirectoryRecord> directories)
    {
        var paths = new List<string>();
        foreach (var dir in directories)
        {
            var start = (int)dir.Offset;
            var end = start + (int)dir.Size;
            if (end > bytes.Length)
            {
                throw new InvalidDataException("目录路径数据超出解压后的 directory bundle");
            }
            var slice = new byte[dir.Size];
            Array.Copy(bytes, start, slice, 0, (int)dir.Size);
            BuildPaths(slice, paths);
        }
        return paths;
    }

    /// <summary>
    /// 从 directory 数据块构建路径。
    /// 对应 Rust: build_paths(bytes, files)。
    ///
    /// 数据格式：交替的 index(u32) + null 结尾 UTF-8 字符串。
    /// - index == 0：切换 generation_phase；进入 generation 时清空 table
    /// - index &gt; 0：text = table[index-1] + suffix（若 index &lt;= table.len）或 suffix
    ///   - generation_phase：加入 table
    ///   - 非 generation_phase：加入 files
    /// </summary>
    private static void BuildPaths(byte[] bytes, List<string> files)
    {
        using var ms = new MemoryStream(bytes, false);
        using var br = new BinaryReader(ms);

        var generationPhase = false;
        var table = new List<string>();

        while (ms.Position + 4 <= bytes.Length)
        {
            var index = br.ReadUInt32();
            if (index == 0)
            {
                generationPhase = !generationPhase;
                if (generationPhase)
                {
                    table.Clear();
                }
                continue;
            }

            var suffix = ReadNulUtf8(br);
            string text;
            if (index <= (uint)table.Count)
            {
                text = table[(int)index - 1] + suffix;
            }
            else
            {
                text = suffix;
            }

            if (generationPhase)
            {
                table.Add(text);
            }
            else
            {
                files.Add(text);
            }
        }
    }

    /// <summary>
    /// 读取 null 结尾的 UTF-8 字符串。
    /// 对应 Rust: read_nul_utf8(cursor)。
    /// </summary>
    private static string ReadNulUtf8(BinaryReader br)
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            var b = br.ReadByte();
            if (b == 0)
            {
                break;
            }
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    #region 路径哈希

    /// <summary>
    /// 按 hash 模式计算路径哈希。
    /// 对应 Rust: hash_path(hash_mode, path)。
    /// </summary>
    private static ulong HashPath(HashMode mode, string path)
    {
        var normalized = ToAsciiLower(TrimEndChar(path, '/'));
        return mode switch
        {
            HashMode.Murmur64A => MurmurHash64A(Encoding.UTF8.GetBytes(normalized)),
            HashMode.Fnv1A => Fnv1aBundleHash(path),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>
    /// FNV-1A bundle 哈希：路径小写 + 去尾部 '/' + "++" 后缀 → FNV-1A。
    /// 对应 Rust: fnv1a_bundle_hash(path)。
    /// </summary>
    private static ulong Fnv1aBundleHash(string path)
    {
        var normalized = ToAsciiLower(TrimEndChar(path, '/')) + "++";
        return HashFnv1a(Encoding.UTF8.GetBytes(normalized));
    }

    /// <summary>
    /// 标准 FNV-1A 64 位哈希。
    /// 对应 Rust: hash_fnv1a(data)。
    /// </summary>
    private static ulong HashFnv1a(byte[] data)
    {
        unchecked
        {
            ulong hash = 0xcbf29ce484222325UL;
            foreach (var b in data)
            {
                hash ^= b;
                hash *= 0x100000001b3UL;
            }
            return hash;
        }
    }

    /// <summary>
    /// Murmur64A 哈希。
    /// 对应 Rust: murmur_hash64a(data)。
    /// </summary>
    private static ulong MurmurHash64A(byte[] data)
    {
        unchecked
        {
            if (data.Length == 0)
            {
                return 0xF42A94E69CFF42FE;
            }
            const ulong M = 0xC6A4A7935BD1E995UL;
            const int R = 47;
            ulong hash = 0x1337B33FUL ^ ((ulong)data.Length * M);

            var chunks = data.Length / 8;
            for (var i = 0; i < chunks; i++)
            {
                var k = BitConverter.ToUInt64(data, i * 8);
                k *= M;
                k ^= k >> R;
                k *= M;
                hash ^= k;
                hash *= M;
            }

            var remStart = chunks * 8;
            var remaining = data.Length - remStart;
            if (remaining > 0)
            {
                ulong tail = 0;
                for (var i = 0; i < remaining; i++)
                {
                    tail |= (ulong)data[remStart + i] << (i * 8);
                }
                hash ^= tail;
                hash *= M;
            }

            hash ^= hash >> R;
            hash *= M;
            hash ^= hash >> R;
            return hash;
        }
    }

    /// <summary>
    /// ASCII 小写转换，匹配 Rust 的 to_ascii_lowercase()。
    /// 仅转换 A-Z，非 ASCII 字符保持不变。
    /// </summary>
    private static string ToAsciiLower(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= 'A' && chars[i] <= 'Z')
            {
                chars[i] = (char)(chars[i] + 32);
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// 移除所有尾部指定字符。
    /// 匹配 Rust 的 trim_end_matches('/')。
    /// </summary>
    private static string TrimEndChar(string s, char c)
    {
        var end = s.Length;
        while (end > 0 && s[end - 1] == c)
        {
            end--;
        }
        return end < s.Length ? s.Substring(0, end) : s;
    }

    #endregion
}
