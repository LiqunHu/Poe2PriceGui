using System.Windows;

namespace Poe2PriceGui.Windows;

/// <summary>
/// 新建过滤器时的命名对话框。
/// </summary>
public partial class NewFilterNameWindow : Window
{
    /// <summary>用户输入的过滤器名称。</summary>
    public string FilterName { get; private set; } = "";

    public NewFilterNameWindow(string defaultName)
    {
        InitializeComponent();
        FilterName = defaultName;

        Loaded += (_, _) =>
        {
            NameTextBox.Text = FilterName;
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        FilterName = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(FilterName))
        {
            MessageBox.Show("请输入过滤器名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }
}
