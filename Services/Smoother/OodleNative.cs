using System.Runtime.InteropServices;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// Oodle 压缩/解压 P/Invoke 封装。
///
/// 使用项目自带的 oo2core.dll（RAD Game Tools 闭源，与 BundleExtractor.exe /
/// PatchBundle3.exe 共用同一份）。该 DLL 同时支持压缩和解压——本项目已有的物价补丁
/// 写入功能（通过 PatchBundle3.exe）正是基于此 DLL 的压缩能力实现的。
///
/// P/Invoke 签名参考 VisualGGPK2 / LibBundle（Oodle 9 API）:
/// https://github.com/aianlinb/VisualGGPK2/blob/ee8a5a415cc5bb749e41489b4b0143551097a1f4/LibBundle/BundleContainer.cs#L10
///
/// 注意：Oodle 9 的 OodleLZ_Compress 是 10 参数签名（无 dstLen，有 scratch buffer），
/// 与标准 Oodle API 的 9 参数签名不同。早期使用 9 参数签名调用会触发
/// AccessViolationException，原因正是参数布局不匹配。
/// </summary>
internal static class OodleNative
{
    /// <summary>
    /// Oodle 压缩器：Mermaid。
    /// 对应 Rust: OODLE_MERMAID_COMPRESSOR = 9。
    /// </summary>
    public const int OODLE_MERMAID_COMPRESSOR = 9;

    /// <summary>
    /// Oodle 压缩级别：SuperFast（1）。
    /// 对应 Rust: OODLE_COMPRESS_LEVEL = 1。
    /// </summary>
    public const int OODLE_COMPRESS_LEVEL = 1;

    #region 解压

    /// <summary>
    /// oo2core.dll 的 OodleLZ_Decompress 14 参数签名（Oodle 9 API）。
    /// 对应 VisualGGPK2: OodleLZ_Decompress(buffer, bufferSize, result, outputBufferSize,
    ///     a, b, c, d, e, f, g, h, i, ThreadModule)。
    /// 返回解压后字节数，&lt;0 表示失败。
    /// </summary>
    [DllImport("oo2core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OodleLZ_Decompress(
        byte[] buffer, int bufferSize,
        byte[] result, long outputBufferSize,
        int a, int b, int c,
        IntPtr d, long e,
        IntPtr f, IntPtr g, IntPtr h,
        long i, int ThreadModule);

    /// <summary>
    /// 解压一个 Oodle 压缩块。
    /// 对应 Rust: Ooz_Decompress(src, srcLen, dst, dstSize)。
    /// 返回实际解压字节数；&lt;0 或与 dstSize 不符视为失败。
    /// </summary>
    public static int Decompress(byte[] src, int srcLen, byte[] dst, int dstSize)
    {
        return OodleLZ_Decompress(
            src, srcLen,
            dst, dstSize,
            0, 0, 0,
            IntPtr.Zero, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            0, 3);
    }

    /// <summary>
    /// 解压并返回新分配的字节数组。
    /// </summary>
    public static byte[] DecompressToAlloc(byte[] src, int srcLen, int dstSize)
    {
        // 与 Rust 参考一致：多分配 64 字节余量，避免 Oodle 越界。
        var dst = new byte[dstSize + 64];
        var wrote = Decompress(src, srcLen, dst, dstSize);
        if (wrote < 0)
        {
            throw new InvalidOperationException($"Oodle 解压失败，返回码 {wrote}");
        }

        Array.Resize(ref dst, dstSize);
        return dst;
    }

    #endregion

    #region 压缩

    /// <summary>
    /// oo2core.dll 的 OodleLZ_Compress 10 参数签名（Oodle 9 API）。
    /// 注意：与标准 Oodle API 的 9 参数签名不同——
    ///   - 没有 dstLen 参数
    ///   - 多了 offs / unused / scratch / scratch_size 四个参数
    ///
    /// 对应 VisualGGPK2: OodleLZ_Compress(format, buffer, bufferSize, outputBuffer,
    ///     level, opts, offs, unused, scratch, scratch_size)。
    /// 返回压缩后字节数，&lt;=0 表示失败。
    /// </summary>
    [DllImport("oo2core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int OodleLZ_Compress(
        int format,
        byte[] buffer, long bufferSize,
        byte[] outputBuffer,
        int level,
        IntPtr opts,
        long offs,
        long unused,
        IntPtr scratch,
        long scratch_size);

    /// <summary>
    /// 压缩一个数据块（使用 Mermaid + SuperFast 级别）。
    /// 对应 Rust: compress_chunk(chunk)。
    ///
    /// 缓冲区大小使用 rawSize + 548（与 VisualGGPK2 参考一致），
    /// 比 libooz 公式（rawSize + 274）略大，更安全。
    /// opts / offs / unused / scratch / scratch_size 全部传 0/NULL
    /// （与 VisualGGPK2 调用方式一致）。
    /// </summary>
    public static byte[] CompressChunk(byte[] chunk)
    {
        if (chunk.Length == 0)
        {
            // Oodle 无法压缩 0 字节，返回空数组（PackUncompressedBundle 会把 chunkCount 至少设为 1）
            return Array.Empty<byte>();
        }

        var capacity = chunk.Length + 548;
        var dst = new byte[capacity];

        var compressedLen = OodleLZ_Compress(
            OODLE_MERMAID_COMPRESSOR,
            chunk, chunk.Length,
            dst,
            OODLE_COMPRESS_LEVEL,
            IntPtr.Zero,    // opts = NULL
            0,              // offs = 0
            0,              // unused = 0
            IntPtr.Zero,    // scratch = NULL
            0);             // scratch_size = 0

        if (compressedLen <= 0)
        {
            throw new InvalidOperationException($"Oodle 压缩失败，返回码 {compressedLen}（输入 {chunk.Length} 字节）");
        }
        Array.Resize(ref dst, compressedLen);
        return dst;
    }

    #endregion
}
