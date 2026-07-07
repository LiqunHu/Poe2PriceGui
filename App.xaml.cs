using System.IO;
using System.Windows;
using Poe2PriceGui.Services;
using Velopack;

namespace Poe2PriceGui;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack 必须在应用启动最早期初始化，处理安装/更新钩子。
        VelopackApp.Build().Run();

        // 1. 准备用户数据目录（%LOCALAPPDATA%\Poe2PriceGui\）。
        AppDataPath.EnsureDirectories();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
