using System.Windows;

namespace Poe2PriceGui.Windows;

/// <summary>
/// 自动更新设置窗口。
/// </summary>
public partial class AutoUpdateSettingsWindow : Window
{
    public AutoUpdateSettingsWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
