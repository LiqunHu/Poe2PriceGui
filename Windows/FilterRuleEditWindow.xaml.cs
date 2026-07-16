using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Windows;

/// <summary>
/// 过滤器规则编辑窗口。支持调色板选色。
/// </summary>
public partial class FilterRuleEditWindow : Window
{
    /// <summary>当前编辑的规则对象（直接修改原对象，确定后保留更改）。</summary>
    public LootFilterRule Rule { get; }

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

    public FilterRuleEditWindow(LootFilterRule rule)
    {
        Rule = rule;
        InitializeComponent();
        DataContext = this;
        InitPresetPalette();
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
}
