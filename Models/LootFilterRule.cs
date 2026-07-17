using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Poe2PriceGui.Models;

/// <summary>
/// 过滤器文件中的一条规则（Show/Hide 块）。
/// </summary>
public class LootFilterRule : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private string _comment = "";
    private string _category = "";
    private string _classCondition = "";
    private string _baseTypeCondition = "";
    private string _rawConditions = "";
    private string _customAlertSound = "";
    private int _fontSize = 40;
    private Color _textColor = Colors.White;
    private Color _borderColor = Colors.Black;
    private Color _backgroundColor = Colors.Black;
    private string _minimapIcon = "";
    private string _minimapIconSize = "0";
    private string _minimapIconColor = "Red";
    private string _minimapIconShape = "Star";
    private bool _hasMinimapIcon;
    private bool _hasPlayEffect;
    private string _playEffectColor = "Blue";
    private bool _isPlayEffectTemp;
    private bool _disableDropSound;
    private string _baseTypeText = "";

    /// <summary>是否显示（Show=true / Hide=false）。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>规则注释（# 后面的文字）。</summary>
    public string Comment
    {
        get => _comment;
        set => SetProperty(ref _comment, value);
    }

    /// <summary>分类（从注释段落标题推断）。</summary>
    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    /// <summary>Class 条件值。</summary>
    public string ClassCondition
    {
        get => _classCondition;
        set => SetProperty(ref _classCondition, value);
    }

    /// <summary>BaseType 条件值（英文原文）。</summary>
    public string BaseTypeCondition
    {
        get => _baseTypeCondition;
        set => SetProperty(ref _baseTypeCondition, value);
    }

    /// <summary>BaseType 的可读文本（用于UI显示）。</summary>
    public string BaseTypeText
    {
        get => _baseTypeText;
        set => SetProperty(ref _baseTypeText, value);
    }

    /// <summary>原始条件文本（除了 Class 和 BaseType 之外的所有条件）。</summary>
    public string RawConditions
    {
        get => _rawConditions;
        set => SetProperty(ref _rawConditions, value);
    }

    /// <summary>自定义提示音路径（统一使用正斜杠，避免下拉列表匹配失败）。</summary>
    public string CustomAlertSound
    {
        get => _customAlertSound;
        set => SetProperty(ref _customAlertSound, (value ?? string.Empty).Replace('\\', '/'));
    }

    /// <summary>字体大小。</summary>
    public int FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, value);
    }

    /// <summary>文字颜色。</summary>
    public Color TextColor
    {
        get => _textColor;
        set => SetProperty(ref _textColor, value);
    }

    /// <summary>边框颜色。</summary>
    public Color BorderColor
    {
        get => _borderColor;
        set => SetProperty(ref _borderColor, value);
    }

    /// <summary>背景颜色。</summary>
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set => SetProperty(ref _backgroundColor, value);
    }

    /// <summary>小地图图标原始设置。</summary>
    public string MinimapIcon
    {
        get => _minimapIcon;
        set => SetProperty(ref _minimapIcon, value);
    }

    /// <summary>是否启用小地图图标。</summary>
    public bool HasMinimapIcon
    {
        get => _hasMinimapIcon;
        set => SetProperty(ref _hasMinimapIcon, value);
    }

    /// <summary>小地图图标尺寸（0=Large, 1=Medium, 2=Small）。</summary>
    public string MinimapIconSize
    {
        get => _minimapIconSize;
        set => SetProperty(ref _minimapIconSize, value);
    }

    /// <summary>小地图图标颜色。</summary>
    public string MinimapIconColor
    {
        get => _minimapIconColor;
        set => SetProperty(ref _minimapIconColor, value);
    }

    /// <summary>小地图图标形状。</summary>
    public string MinimapIconShape
    {
        get => _minimapIconShape;
        set => SetProperty(ref _minimapIconShape, value);
    }

    /// <summary>是否启用光柱（PlayEffect）。</summary>
    public bool HasPlayEffect
    {
        get => _hasPlayEffect;
        set
        {
            if (SetProperty(ref _hasPlayEffect, value))
                OnPropertyChanged(nameof(PlayEffect));
        }
    }

    /// <summary>光柱颜色（Red/Green/Blue 等）。</summary>
    public string PlayEffectColor
    {
        get => _playEffectColor;
        set
        {
            if (SetProperty(ref _playEffectColor, value))
                OnPropertyChanged(nameof(PlayEffect));
        }
    }

    /// <summary>光柱是否为临时效果（Temp）。</summary>
    public bool IsPlayEffectTemp
    {
        get => _isPlayEffectTemp;
        set
        {
            if (SetProperty(ref _isPlayEffectTemp, value))
                OnPropertyChanged(nameof(PlayEffect));
        }
    }

    /// <summary>播放效果设置（由 HasPlayEffect/Color/Temp 组合而成）。</summary>
    public string PlayEffect
    {
        get => HasPlayEffect ? $"{PlayEffectColor}{(IsPlayEffectTemp ? " Temp" : "")}" : "";
        set => ParsePlayEffect(value);
    }

    private void ParsePlayEffect(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            HasPlayEffect = false;
            PlayEffectColor = "Blue";
            IsPlayEffectTemp = false;
            return;
        }

        var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        HasPlayEffect = true;
        PlayEffectColor = parts.Length > 0 ? parts[0] : "Blue";
        IsPlayEffectTemp = parts.Length > 1 && string.Equals(parts[1], "Temp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是否禁用掉落音效。</summary>
    public bool DisableDropSound
    {
        get => _disableDropSound;
        set => SetProperty(ref _disableDropSound, value);
    }

    /// <summary>对应的崇高石价格（由自动更新功能填充，0 表示无价格数据）。</summary>
    public decimal PriceExalted { get; set; }

    /// <summary>是否匹配到价格数据。</summary>
    public bool HasPriceData => PriceExalted > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// 过滤器文件分类。
/// </summary>
public class LootFilterCategory
{
    public string Name { get; set; } = "";
    public ObservableCollection<LootFilterRule> Rules { get; set; } = new();
}
