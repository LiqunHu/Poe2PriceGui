using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Poe2PriceGui.Services;
using Velopack;

namespace Poe2PriceGui;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 0. 第一件事：注册全局异常处理，确保后续任何崩溃都能留下日志。
        RegisterDomainExceptionHandlers();

        try
        {
            // 1. Velopack 必须在应用启动最早期初始化，处理安装/更新钩子。
            WriteStartupLog("VelopackApp.Build().Run() 前");
            VelopackApp.Build().Run();
            WriteStartupLog("VelopackApp.Build().Run() 后");

            // 2. 准备用户数据目录（%LOCALAPPDATA%\Poe2PriceGui\）。
            WriteStartupLog("AppDataPath.EnsureDirectories() 前");
            AppDataPath.EnsureDirectories();
            WriteStartupLog("AppDataPath.EnsureDirectories() 后");

            var app = new App();
            app.InitializeComponent();
            WriteStartupLog("App.Run() 前");
            app.Run();
        }
        catch (Exception ex)
        {
            // 兜底：Main 内任何未捕获异常都到这里。
            LogCrash("Main.try-catch", ex);
            ShowCrashDialog(ex);
        }
    }

    public App()
    {
        // UI 线程异常处理（实例事件，需要 App 实例）。
        DispatcherUnhandledException += (sender, e) =>
        {
            LogCrash("DispatcherUnhandledException", e.Exception);
            ShowCrashDialog(e.Exception);
            e.Handled = true;
        };

        // 进程退出检测：无论正常退出还是 Environment.Exit 都会触发。
        // 如果这里触发而 crash log 没有，说明是"正常退出"而非崩溃。
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            WriteStartupLog($"!!! ProcessExit 触发，进程正在退出 !!!");
        };

        // WPF 应用正常退出。
        Exit += (sender, e) =>
        {
            WriteStartupLog($"!!! Application.Exit 触发，退出码={e.ApplicationExitCode} !!!");
        };

        // Windows 会话结束（关机/注销）。
        SessionEnding += (sender, e) =>
        {
            WriteStartupLog($"!!! SessionEnding 触发: {e.ReasonSessionEnding} !!!");
        };

        // 手动创建 MainWindow，全程 try-catch + 步骤追踪。
        Startup += OnStartup;
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            WriteStartupLog("MainWindow 构造前");
            var window = new MainWindow();
            WriteStartupLog("MainWindow 构造后（InitializeComponent + ViewModel 完成）");
            window.Show();
            WriteStartupLog("MainWindow.Show() 后");
            MainWindow = window;
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            ShowCrashDialog(ex);
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// 注册非 UI 线程的全局异常处理，必须最先调用。
    /// </summary>
    private static void RegisterDomainExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogCrash("AppDomain.UnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// 启动步骤追踪：每一步前后都写一行，闪退时可看日志定位卡在哪一步。
    /// </summary>
    private static void WriteStartupLog(string step)
    {
        try
        {
            var logFile = ResolveEarlyLogFile("startup.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {step}\r\n", Encoding.UTF8);
        }
        catch
        {
            // 追踪日志失败不应影响启动。
        }
    }

    /// <summary>
    /// 把崩溃异常写入 crash_*.log。
    /// 优先用 AppDataPath.Logs，若不可用（AppDataPath 构造崩溃）则回退到 exe 同目录。
    /// </summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var logDir = ResolveEarlyLogDir();
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"==== {source} ====");
            sb.AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Version: {GetAppVersion()}");
            sb.AppendLine($"OS:      {Environment.OSVersion}");
            sb.AppendLine($"Runtime: {Environment.Version}");
            sb.AppendLine($"PID:     {Environment.ProcessId}");
            sb.AppendLine($"ExePath: {Environment.ProcessPath}");
            sb.AppendLine($"CWD:     {Environment.CurrentDirectory}");
            sb.AppendLine();
            AppendException(sb, ex, 0);
            sb.AppendLine();

            File.WriteAllText(logFile, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 日志本身失败时只能放弃。
        }
    }

    /// <summary>
    /// 解析早期日志目录：优先 AppDataPath.Logs，若访问失败则回退到 exe 同目录下的 logs。
    /// 这样即使 AppDataPath 静态构造崩溃也能写日志。
    /// </summary>
    private static string ResolveEarlyLogDir()
    {
        try
        {
            // 直接访问 AppDataPath.Logs 可能触发其静态构造。
            var logs = AppDataPath.Logs;
            if (!string.IsNullOrEmpty(logs))
                return logs;
        }
        catch
        {
            // AppDataPath 静态构造崩溃，走 fallback。
        }

        // Fallback: exe 同目录下的 logs。
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return Path.Combine(exeDir, "logs");
    }

    private static string ResolveEarlyLogFile(string fileName)
    {
        return Path.Combine(ResolveEarlyLogDir(), fileName);
    }

    private static void AppendException(StringBuilder sb, Exception? ex, int depth)
    {
        if (ex is null) return;
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}Type:    {ex.GetType().FullName}");
        sb.AppendLine($"{indent}Message: {ex.Message}");
        sb.AppendLine($"{indent}Source:  {ex.Source}");
        if (ex.TargetSite is not null)
            sb.AppendLine($"{indent}Method:  {ex.TargetSite.DeclaringType?.FullName}.{ex.TargetSite.Name}");
        sb.AppendLine($"{indent}StackTrace:");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            foreach (var line in ex.StackTrace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                sb.AppendLine($"{indent}  {line}");
        }
        if (ex is AggregateException ae && ae.InnerExceptions.Count > 0)
        {
            for (int i = 0; i < ae.InnerExceptions.Count; i++)
            {
                sb.AppendLine($"{indent}Inner[{i}]:");
                AppendException(sb, ae.InnerExceptions[i], depth + 1);
            }
        }
        else if (ex.InnerException is not null)
        {
            sb.AppendLine($"{indent}Inner:");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }

    /// <summary>
    /// 弹窗提示用户崩溃信息，并提供日志路径方便反馈。
    /// </summary>
    private static void ShowCrashDialog(Exception ex)
    {
        var logDir = ResolveEarlyLogDir();
        var msg = $"程序遇到意外错误：\n\n{ex.GetType().Name}: {ex.Message}\n\n崩溃日志已保存到：\n{logDir}\n\n要继续运行吗？（继续可能导致不稳定，建议重启）";
        try
        {
            var result = MessageBox.Show(msg, "Poe2PriceGui 崩溃报告", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (result != MessageBoxResult.Yes)
            {
                Environment.Exit(1);
            }
        }
        catch
        {
            // 弹窗本身失败（例如非 UI 线程），直接退出。
            Environment.Exit(1);
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? "?";
        }
        catch
        {
            return "?";
        }
    }
}
