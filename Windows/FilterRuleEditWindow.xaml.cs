using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Poe2PriceGui.Models;
using Poe2PriceGui.Services;
using System.Linq;

namespace Poe2PriceGui.Windows;

/// <summary>
/// 过滤器规则编辑窗口。支持调色板选色。
/// </summary>
public partial class FilterRuleEditWindow : Window
{
    /// <summary>当前编辑的规则对象（直接修改原对象，确定后保留更改）。</summary>
    public LootFilterRule Rule { get; }

    /// <summary>可用的音效文件列表（从游戏过滤器目录扫描到的 MP3）。</summary>
    public IEnumerable<string> AvailableSounds { get; }

    /// <summary>小地图图标尺寸选项（POE 语法：0=Large, 1=Medium, 2=Small）。</summary>
    public IReadOnlyList<string> MinimapIconSizes { get; } = new[] { "0", "1", "2" };

    /// <summary>小地图图标颜色选项（参考 chromatic-poe-main）。</summary>
    public IReadOnlyList<string> MinimapIconColors { get; } = new[]
    {
        "Red", "Green", "Blue", "Brown", "White", "Yellow",
        "Cyan", "Grey", "Orange", "Pink", "Purple"
    };

    /// <summary>小地图图标形状选项（参考 chromatic-poe-main）。</summary>
    public IReadOnlyList<string> MinimapIconShapes { get; } = new[]
    {
        "Circle", "Diamond", "Hexagon", "Square", "Star", "Triangle",
        "Cross", "Moon", "Raindrop", "Kite", "Pentagon", "UpsideDownHouse"
    };

    /// <summary>物品类别快速选择列表（英文值 + 中文显示名）。</summary>
    public IReadOnlyList<FilterDataService.LocalizedItem> ItemClasses => FilterDataService.ItemClasses;

    /// <summary>通货名称快速选择列表（英文值 + 中文显示名）。</summary>
    public IReadOnlyList<FilterDataService.LocalizedItem> CurrencyNames => FilterDataService.CurrencyNames;

    /// <summary>基础类型快速选择列表（英文值 + 中文显示名）。</summary>
    public IReadOnlyList<FilterDataService.LocalizedItem> BaseTypeNames => FilterDataService.BaseTypeNames;

    /// <summary>光柱颜色选项。</summary>
    public IReadOnlyList<string> PlayEffectColors { get; } = new[]
    {
        "Red", "Green", "Blue", "Brown", "White", "Yellow",
        "Cyan", "Grey", "Orange", "Pink", "Purple"
    };

    /// <summary>小地图图标尺寸下拉项（值=POE语法，显示=中文）。</summary>
    public IReadOnlyList<ComboItem> MinimapIconSizeItems { get; } = new[]
    {
        new ComboItem { Value = "0", Display = "大 (Large)" },
        new ComboItem { Value = "1", Display = "中 (Medium)" },
        new ComboItem { Value = "2", Display = "小 (Small)" }
    };

    /// <summary>小地图图标颜色下拉项（值=POE语法，显示=中文）。</summary>
    public IReadOnlyList<ComboItem> MinimapIconColorItems { get; } = new[]
    {
        new ComboItem { Value = "Red", Display = "红" },
        new ComboItem { Value = "Green", Display = "绿" },
        new ComboItem { Value = "Blue", Display = "蓝" },
        new ComboItem { Value = "Brown", Display = "棕" },
        new ComboItem { Value = "White", Display = "白" },
        new ComboItem { Value = "Yellow", Display = "黄" },
        new ComboItem { Value = "Cyan", Display = "青" },
        new ComboItem { Value = "Grey", Display = "灰" },
        new ComboItem { Value = "Orange", Display = "橙" },
        new ComboItem { Value = "Pink", Display = "粉" },
        new ComboItem { Value = "Purple", Display = "紫" }
    };

    /// <summary>小地图图标形状下拉项（值=POE语法，显示=中文）。</summary>
    public IReadOnlyList<ComboItem> MinimapIconShapeItems { get; } = new[]
    {
        new ComboItem { Value = "Circle", Display = "圆形" },
        new ComboItem { Value = "Diamond", Display = "菱形" },
        new ComboItem { Value = "Hexagon", Display = "六边形" },
        new ComboItem { Value = "Square", Display = "方形" },
        new ComboItem { Value = "Star", Display = "星形" },
        new ComboItem { Value = "Triangle", Display = "三角形" },
        new ComboItem { Value = "Cross", Display = "十字" },
        new ComboItem { Value = "Moon", Display = "月亮" },
        new ComboItem { Value = "Raindrop", Display = "水滴" },
        new ComboItem { Value = "Kite", Display = "风筝" },
        new ComboItem { Value = "Pentagon", Display = "五边形" },
        new ComboItem { Value = "UpsideDownHouse", Display = "倒房子" }
    };

    /// <summary>光柱颜色下拉项（值=POE语法，显示=中文）。</summary>
    public IReadOnlyList<ComboItem> PlayEffectColorItems => MinimapIconColorItems;

    /// <summary>当前选中的快速 Class。</summary>
    public string? SelectedQuickClass { get; set; }

    /// <summary>当前选中的快速 BaseType。</summary>
    public string? SelectedQuickBaseType { get; set; }

    /// <summary>当前选中的快速 Currency。</summary>
    public string? SelectedQuickCurrency { get; set; }

    /// <summary>音效下拉框的过滤视图。</summary>
    public ICollectionView FilteredAvailableSounds { get; }

    /// <summary>Class 快速选择的过滤视图。</summary>
    public ICollectionView FilteredItemClasses { get; }

    /// <summary>BaseType 快速选择的过滤视图。</summary>
    public ICollectionView FilteredBaseTypeNames { get; }

    /// <summary>Currency 快速选择的过滤视图。</summary>
    public ICollectionView FilteredCurrencyNames { get; }

    /// <summary>调色板当前正在编辑的目标颜色属性名。</summary>
    private string? _editingColorTarget;

    /// <summary>调色板当前颜色（编辑中）。</summary>
    private Color _editingColor;

    // 预设色板：参考 POE 过滤器常用色 + 通用调色板
    private static readonly Color[] PresetColors =
    {
        // 红色系
        Color.FromRgb(217,4,31), Color.FromRgb(198,91,57), Color.FromRgb(255,0,0), Color.FromRgb(180,0,0),
        // 粉/紫
        Color.FromRgb(218,8,98), Color.FromRgb(255,0,255), Color.FromRgb(135,28,226), Color.FromRgb(128,0,255),
        // 蓝色系
        Color.FromRgb(0,0,255), Color.FromRgb(0,150,255), Color.FromRgb(30,90,200), Color.FromRgb(0,200,200),
        // 绿色系
        Color.FromRgb(0,200,0), Color.FromRgb(0,150,0), Color.FromRgb(50,180,100), Color.FromRgb(0,255,128),
        // 黄/橙
        Color.FromRgb(255,236,43), Color.FromRgb(255,200,0), Color.FromRgb(255,165,0), Color.FromRgb(200,120,0),
        // 白/灰/黑
        Color.FromRgb(255,255,255), Color.FromRgb(200,200,200), Color.FromRgb(128,128,128), Color.FromRgb(0,0,0),
        // 棕色系
        Color.FromRgb(150,100,50), Color.FromRgb(120,80,40), Color.FromRgb(100,60,30), Color.FromRgb(80,50,20),
        // 青色系
        Color.FromRgb(0,128,128), Color.FromRgb(64,128,128), Color.FromRgb(0,180,180), Color.FromRgb(100,200,200),
        // 深色系
        Color.FromRgb(30,30,60), Color.FromRgb(40,20,40), Color.FromRgb(60,40,20), Color.FromRgb(20,40,30),
        // 浅色系
        Color.FromRgb(255,200,200), Color.FromRgb(200,255,200), Color.FromRgb(200,200,255), Color.FromRgb(255,255,200),
        // 特殊色
        Color.FromRgb(255,100,100), Color.FromRgb(100,255,100), Color.FromRgb(100,100,255), Color.FromRgb(255,255,100),
        Color.FromRgb(100,255,255), Color.FromRgb(255,100,255), Color.FromRgb(180,180,100), Color.FromRgb(100,180,180),
    };

    public FilterRuleEditWindow(LootFilterRule rule, IEnumerable<string>? availableSounds = null)
    {
        Rule = rule;
        AvailableSounds = availableSounds ?? Array.Empty<string>();

        FilteredAvailableSounds = CollectionViewSource.GetDefaultView(AvailableSounds);
        FilteredItemClasses = CollectionViewSource.GetDefaultView(ItemClasses);
        FilteredBaseTypeNames = CollectionViewSource.GetDefaultView(BaseTypeNames);
        FilteredCurrencyNames = CollectionViewSource.GetDefaultView(CurrencyNames);

        InitializeComponent();
        DataContext = this;
        InitPresetPalette();
        SetupComboBoxFiltering();
        // RGB 滑块联动
        SliderR.ValueChanged += (_, _) => OnSliderChanged();
        SliderG.ValueChanged += (_, _) => OnSliderChanged();
        SliderB.ValueChanged += (_, _) => OnSliderChanged();
        TextBoxR.LostFocus += (_, _) => OnTextBoxChanged();
        TextBoxG.LostFocus += (_, _) => OnTextBoxChanged();
        TextBoxB.LostFocus += (_, _) => OnTextBoxChanged();
    }

    /// <summary>滑块值变化：同步文本框和预览。</summary>
    private void OnSliderChanged()
    {
        if (!ColorPopup.IsOpen) return;
        _editingColor = Color.FromRgb((byte)SliderR.Value, (byte)SliderG.Value, (byte)SliderB.Value);
        TextBoxR.Text = _editingColor.R.ToString();
        TextBoxG.Text = _editingColor.G.ToString();
        TextBoxB.Text = _editingColor.B.ToString();
        PreviewColorBox.Background = new SolidColorBrush(_editingColor);
    }

    /// <summary>文本框编辑：同步滑块和预览。</summary>
    private void OnTextBoxChanged()
    {
        if (!ColorPopup.IsOpen) return;
        if (byte.TryParse(TextBoxR.Text, out byte r) &&
            byte.TryParse(TextBoxG.Text, out byte g) &&
            byte.TryParse(TextBoxB.Text, out byte b))
        {
            _editingColor = Color.FromRgb(r, g, b);
            SliderR.Value = r;
            SliderG.Value = g;
            SliderB.Value = b;
            PreviewColorBox.Background = new SolidColorBrush(_editingColor);
        }
    }

    /// <summary>为可编辑 ComboBox 注册输入筛选事件；使用防抖避免频繁刷新导致卡顿。
    /// 直接订阅内部 TextBox 的 TextChanged，避免 WPF ComboBox.Text/SelectedItem 同步导致删除后文本被恢复。</summary>
    private void SetupComboBoxFiltering()
    {
        void AttachFilter(ComboBox combo, ICollectionView view, Func<object, string> getText)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
            string? pendingText = null;
            var isSelecting = false;

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var text = pendingText ?? "";
                view.Filter = string.IsNullOrWhiteSpace(text)
                    ? null
                    : obj => getText(obj).Contains(text, StringComparison.OrdinalIgnoreCase);
                view.Refresh();

                var count = view.Cast<object>().Count();
                if (count > 0)
                {
                    if (!combo.IsDropDownOpen)
                        combo.IsDropDownOpen = true;
                }
                else if (combo.IsDropDownOpen)
                {
                    combo.IsDropDownOpen = false;
                }
            };

            void HookTextBox()
            {
                if (combo.Template?.FindName("PART_EditableTextBox", combo) is not TextBox textBox)
                    return;

                textBox.TextChanged += (_, _) =>
                {
                    if (isSelecting) return;
                    pendingText = textBox.Text;
                    timer.Stop();
                    timer.Start();
                };
            }

            if (combo.IsLoaded)
                HookTextBox();
            else
                combo.Loaded += (_, _) => HookTextBox();

            // 鼠标/Enter 从下拉列表选择项时，会同步更新 TextBox 文本；
            // 此时应忽略 TextChanged，避免选择后又把下拉框弹出来。
            combo.SelectionChanged += (_, _) =>
            {
                isSelecting = true;
                timer.Stop();
                Dispatcher.BeginInvoke(() => isSelecting = false, DispatcherPriority.Background);
            };

            combo.DropDownClosed += (_, _) =>
            {
                timer.Stop();
                view.Filter = null;
                view.Refresh();
            };

            combo.Unloaded += (_, _) => timer.Stop();
        }

        AttachFilter(SoundCombo, FilteredAvailableSounds, x => x as string ?? "");
        AttachFilter(QuickClassCombo, FilteredItemClasses, x => (x as FilterDataService.LocalizedItem)?.Chinese ?? "");
        AttachFilter(QuickBaseTypeCombo, FilteredBaseTypeNames, x => (x as FilterDataService.LocalizedItem)?.Chinese ?? "");
        AttachFilter(QuickCurrencyCombo, FilteredCurrencyNames, x => (x as FilterDataService.LocalizedItem)?.Chinese ?? "");
    }

    /// <summary>初始化预设色板。</summary>
    private void InitPresetPalette()
    {
        foreach (var color in PresetColors)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 22,
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                Tag = color,
            };
            swatch.MouseLeftButtonDown += PresetSwatch_Click;
            PresetPalette.Children.Add(swatch);
        }
    }

    /// <summary>点击预设色块：立即应用到正在编辑的颜色。</summary>
    private void PresetSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is Color c)
        {
            _editingColor = c;
            UpdateColorEditors();
        }
    }

    /// <summary>点击色块：打开调色板弹窗。</summary>
    private void ColorBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            _editingColorTarget = border.Name switch
            {
                nameof(TextColorBox) => "Text",
                nameof(BorderColorBox) => "Border",
                nameof(BackgroundColorBox) => "Background",
                _ => null
            };
            if (_editingColorTarget == null) return;

            // 初始化为当前颜色
            _editingColor = _editingColorTarget switch
            {
                "Text" => Rule.TextColor,
                "Border" => Rule.BorderColor,
                "Background" => Rule.BackgroundColor,
                _ => Colors.White
            };
            UpdateColorEditors();
            ColorPopup.IsOpen = true;
        }
    }

    /// <summary>同步 RGB 滑块/文本框/预览色块。</summary>
    private void UpdateColorEditors()
    {
        SliderR.Value = _editingColor.R;
        SliderG.Value = _editingColor.G;
        SliderB.Value = _editingColor.B;
        TextBoxR.Text = _editingColor.R.ToString();
        TextBoxG.Text = _editingColor.G.ToString();
        TextBoxB.Text = _editingColor.B.ToString();
        PreviewColorBox.Background = new SolidColorBrush(_editingColor);
    }

    /// <summary>调色板确定按钮：把编辑中的颜色应用到规则。</summary>
    private void ColorConfirm_Click(object sender, RoutedEventArgs e)
    {
        // 从滑块读取最终颜色
        _editingColor = Color.FromRgb((byte)SliderR.Value, (byte)SliderG.Value, (byte)SliderB.Value);
        switch (_editingColorTarget)
        {
            case "Text":
                Rule.TextColor = _editingColor;
                break;
            case "Border":
                Rule.BorderColor = _editingColor;
                break;
            case "Background":
                Rule.BackgroundColor = _editingColor;
                break;
        }
        ColorPopup.IsOpen = false;
    }

    /// <summary>确定按钮：关闭窗口。</summary>
    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>应用快速选择的 Class 到规则。</summary>
    private void ApplyQuickClass_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedQuickClass))
            return;
        Rule.ClassCondition = SelectedQuickClass;
    }

    /// <summary>应用快速选择的 BaseType 到规则。</summary>
    private void ApplyQuickBaseType_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedQuickBaseType))
            return;
        Rule.BaseTypeCondition = $"\"{SelectedQuickBaseType}\"";
        Rule.BaseTypeText = SelectedQuickBaseType;
    }

    /// <summary>应用快速选择的 Currency 到规则（自动设置 Class 为 Currency 并填充 BaseType）。</summary>
    private void ApplyQuickCurrency_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedQuickCurrency))
            return;
        Rule.ClassCondition = "Currency";
        Rule.BaseTypeCondition = $"\"{SelectedQuickCurrency}\"";
        Rule.BaseTypeText = SelectedQuickCurrency;
    }

    /// <summary>下拉框选项键值对（值=POE语法，显示=中文）。</summary>
    public class ComboItem
    {
        public string Value { get; set; } = "";
        public string Display { get; set; } = "";
    }
}
