using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 泥人补丁标识，对应 tiny-poe2smoother 的 PatchId 枚举。
/// </summary>
public enum PatchId
{
    Camera,
    Minimap,
    AtlasFog,
    Fog,
    Rain,
    Clouds,
    EnvParticles,
    Shadow,
    Light,
    Delirium,
    Particles,
    Effects,
    DisableSounds,
    SkillSounds,
    MonsterSounds,
    MtxSoft,

    Effects_New,
    /// <summary>地毯式补丁：清空全部 .epk + 简化全部 .ao，覆盖整个 metadata/ 目录。</summary>
    Blanket,
    /// <summary>测试补丁：按 TinyBundle 实际覆盖的 9 个 metadata 子目录选择 .epk/.ao 文件，用于与 TinyBundle TSV 对比验证。</summary>
    Test,
    EffectNone,
}

/// <summary>
/// 补丁元数据。
/// </summary>
public sealed class PatchInfo
{
    public PatchId Id { get; init; }
    /// <summary>英文标识符，用于持久化和命令行解析（不可改）。</summary>
    public string Name { get; init; } = "";
    /// <summary>中文显示名，用于 UI 展示。</summary>
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>是否为高风险补丁（UI 显示红色 + "(危)" 后缀）。这类补丁可能触发封号或崩溃风险。</summary>
    public bool IsDangerous { get; init; }

    ///<summary>组，用于 UI 单选框展示。</summary>
    public string GroupName { get; init; } = "";
}

/// <summary>
/// UI 绑定用：补丁元数据 + 勾选状态。实现 INotifyPropertyChanged 以支持双向绑定。
/// 支持单选组模式（用于 effects 互斥选择）。
/// </summary>
public sealed class PatchSelectionItem : INotifyPropertyChanged
{
    private bool _isChecked;

    public PatchInfo Info { get; }

    /// <summary>该补丁是否属于单选组。同一 GroupName 下只有一个能被勾选。</summary>
    public bool IsRadio => !string.IsNullOrEmpty(GroupName);

    /// <summary>单选组名。相同 GroupName 的补丁在 UI 上显示为 RadioButton 并互斥。</summary>
    public string GroupName { get; }

    public PatchSelectionItem(PatchInfo info, bool isChecked = false, string? groupName = null)
    {
        Info = info;
        _isChecked = isChecked;
        GroupName = groupName ?? "";
    }

    /// <summary>当前是否被勾选。设置时触发 PropertyChanged 以驱动 UI 双向绑定。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 预设元数据。
/// </summary>
public sealed class PresetInfo
{
    /// <summary>英文标识符，用于命令参数传递（不可改）。</summary>
    public string Name { get; init; } = "";
    /// <summary>中文显示名，用于 UI 展示。</summary>
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public PatchId[] Patches { get; init; } = [];
    /// <summary>是否在 UI 中隐藏（不显示为预设按钮）。隐藏后仍可通过命令行/代码调用。</summary>
    public bool IsHidden { get; init; }
}

/// <summary>
/// 单个文件的补丁变更记录。
/// </summary>
public sealed class PatchChange
{
    public string Path { get; init; } = "";
    public string BundleName { get; init; } = "";
    public int OldSize { get; init; }
    public int NewSize { get; init; }
}

/// <summary>
/// 补丁集合：变更记录 + 按 bundle 分组的替换内容。
/// </summary>
public sealed class PatchSet
{
    public List<PatchChange> Changes { get; set; } = [];
    public Dictionary<string, List<(BundleFile File, byte[] Data)>> Replacements { get; set; } = [];
}

/// <summary>
/// 泥人补丁生成报告。
/// </summary>
public sealed class SmootherPatchReport
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int ChangedFileCount { get; set; }
    public List<PatchChange> Changes { get; set; } = [];
    public string OutputZipPath { get; set; } = "";
    public Dictionary<PatchId, int> PatchHitCounts { get; set; } = [];

    /// <summary>
    /// 创建一个失败报告，用于不需要走完整 ComputeReport 流程的快速失败场景
    /// （如 GGPK 模式下 IsPatchApplied 预检查在后台线程里发现已应用）。
    /// </summary>
    public static SmootherPatchReport CreateFailure(string errorMessage)
    {
        return new SmootherPatchReport
        {
            Success = false,
            ErrorMessage = errorMessage,
        };
    }
}

/// <summary>
/// bundle 中的一个文件记录（对应 Rust 的 BundleFile）。
/// </summary>
public sealed class BundleFile
{
    public ulong Hash { get; set; }
    public uint BundleIndex { get; set; }
    public string BundleName { get; set; } = "";
    public uint Offset { get; set; }
    public uint Size { get; set; }
    /// <summary>该文件记录在 index 原始字节中的起始位置（用于更新记录）。</summary>
    public int RecordPos { get; set; }
}

/// <summary>
/// index 中 bundle 的元信息。
/// </summary>
internal sealed class BundleInfo
{
    public string Name { get; set; } = "";
    public uint UncompressedSize { get; set; }
    public int SizePos { get; set; }
}

/// <summary>
/// 路径哈希算法。
/// </summary>
internal enum HashMode
{
    Murmur64A,
    Fnv1A,
}

/// <summary>
/// index 中的目录记录。
/// </summary>
internal readonly struct DirectoryRecord
{
    public ulong PathHash { get; init; }
    public uint Offset { get; init; }
    public uint Size { get; init; }
    public uint RecursiveSize { get; init; }
}

/// <summary>
/// 带路径信息的索引条目。
/// </summary>
public sealed class IndexedPath
{
    public string Path { get; init; } = "";
    public BundleFile File { get; init; } = null!;
}

/// <summary>
/// 泥人补丁执行进度。
/// Description: 当前阶段的简短描述（显示在进度条上方）。
/// Percent: 0-100，整体进度百分比。
/// </summary>
public sealed class SmootherProgress
{
    public string Description { get; init; } = "";
    public int Percent { get; init; }
}

/// <summary>
/// 泥人补丁详细状态：检测我们的补丁和别人的补丁的覆盖情况。
/// </summary>
public sealed class SmootherDetailedStatus
{
    /// <summary>我们的补丁（TinyPoe2Smoother/）是否已应用。</summary>
    public bool OurApplied { get; set; }
    /// <summary>我们的补丁修改的文件数。</summary>
    public int OurFileCount { get; set; }
    /// <summary>我们的自定义 bundle 数量。</summary>
    public int OurBundleCount { get; set; }
    /// <summary>我们的 bundle 文件是否都存在于磁盘。</summary>
    public bool OurBundleFilesExist { get; set; } = true;
    /// <summary>缺失的 bundle 文件列表。</summary>
    public List<string> MissingBundleFiles { get; set; } = [];

    /// <summary>别人的补丁（TinyBundle/）是否已应用。</summary>
    public bool TheirApplied { get; set; }
    /// <summary>别人的补丁修改的文件数。</summary>
    public int TheirFileCount { get; set; }
    /// <summary>别人的 bundle 数量。</summary>
    public int TheirBundleCount { get; set; }

    /// <summary>抽样检测的文件数。</summary>
    public int SamplesChecked { get; set; }
    /// <summary>抽样中已被修改（清空）的文件数。</summary>
    public int SamplesModified { get; set; }

    /// <summary>错误信息（如有）。</summary>
    public string ErrorMessage { get; set; } = "";

    /// <summary>
    /// 生成人类可读的状态摘要。
    /// </summary>
    public string ToSummary()
    {
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            return $"检测失败：{ErrorMessage}";
        }

        var lines = new List<string>();

        // 我们的补丁状态
        if (OurApplied)
        {
            lines.Add($"我们的补丁：已应用（{OurFileCount} 文件，{OurBundleCount} 个自定义 bundle）");
            if (!OurBundleFilesExist)
            {
                lines.Add($"  ⚠ {MissingBundleFiles.Count} 个 bundle 文件缺失！补丁可能已损坏");
            }
            else
            {
                lines.Add("  ✓ bundle 文件均存在");
            }
        }
        else
        {
            lines.Add("我们的补丁：未应用");
        }

        // 别人的补丁状态
        if (TheirApplied)
        {
            lines.Add($"别人的补丁：已应用（{TheirFileCount} 文件，{TheirBundleCount} 个 TinyBundle）");
        }
        else
        {
            lines.Add("别人的补丁：未应用");
        }

        // 抽样验证
        if (SamplesChecked > 0)
        {
            lines.Add($"抽样验证：{SamplesChecked} 个 .epk 中 {SamplesModified} 个已被清空（{SamplesModified * 100 / SamplesChecked}%）");
        }

        return string.Join("\n", lines);
    }
}
