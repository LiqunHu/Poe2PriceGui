using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xiletrade.Library.Models.Poe.Domain.Parser;
using Xiletrade.Library.Services;
using Xiletrade.Library.Services.Interface;
using Xiletrade.Library.Shared;
using Xiletrade.Library.Shared.Enum;
using Xiletrade.Library.ViewModels.Main.Form;

namespace Poe2PriceGui.Services;

/// <summary>
/// 桥接服务：将 xiletrade.Library 的解析能力接入 Poe2PriceGui。
/// 负责初始化 DataManagerService（PoE2 模式）并将剪贴板文本解析为 FormViewModel。
/// </summary>
public sealed class XiletradePriceService
{
    private static XiletradePriceService? _instance;
    private static readonly object _initLock = new();

    private readonly IServiceProvider _serviceProvider;
    private readonly DataManagerService _dataManager;

    /// <summary>是否已成功初始化。</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>当前解析语言索引（Strings.Culture 数组索引）。</summary>
    public int CurrentLanguage => _dataManager.Config.Options.Language;

    private XiletradePriceService()
    {
        // xiletrade 的 DataManagerService 使用 Path.GetFullPath("Data\\") 加载数据文件，
        // 该路径相对于 Environment.CurrentDirectory 解析。必须将当前目录切换到应用输出目录。
        var baseDir = AppContext.BaseDirectory;
        if (Directory.GetCurrentDirectory() != baseDir)
        {
            Directory.SetCurrentDirectory(baseDir);
        }

        var services = new ServiceCollection();
        services.AddSingleton<DataManagerService>();
        services.AddSingleton<IMessageAdapterService, StubMessageAdapter>();
        services.AddSingleton<INavigationService, StubNavigationService>();
        _serviceProvider = services.BuildServiceProvider();

        _dataManager = _serviceProvider.GetRequiredService<DataManagerService>();
    }

    /// <summary>获取单例实例（首次调用时初始化 DataManagerService）。</summary>
    public static XiletradePriceService Instance
    {
        get
        {
            if (_instance is not null && _instance.IsInitialized)
                return _instance;

            lock (_initLock)
            {
                if (_instance is null || !_instance.IsInitialized)
                {
                    _instance = new XiletradePriceService();
                    _instance.Initialize();
                }
            }
            return _instance;
        }
    }

    private void Initialize()
    {
        if (IsInitialized)
            return;

        // forceGameVersion=2 → Config.Options.GameVersion=1 (PoE2)
        _dataManager.TryInit(2);

        // 关键：设置 Resources.Resources.Culture 使 xiletrade 的 .resx 资源返回正确语言。
        // 仅设 CultureInfo.CurrentUICulture 不够 —— Resources.Designer.cs 的 resourceCulture 字段
        // 是独立的静态字段，ResourceManager.GetString(key, resourceCulture) 当 resourceCulture 非 null 时
        // 直接使用它，完全绕过 CurrentUICulture。
        // InfoDescription.IsPoeItem 通过 General126_ItemClassPrefix 判断物品类别行，
        // 若文化不匹配（en-US 返回 "Item Class:"），国服中文文本 "物品类别：" 会被判为非 POE 物品。
        ApplyCulture(_dataManager.Config.Options.Language);

        IsInitialized = true;
    }

    /// <summary>
    /// 切换解析语言并重新加载数据文件。
    /// </summary>
    /// <param name="langIndex">Strings.Culture 数组索引（0=en-US, 9=zh-CN, 10=ja-JP, ...）</param>
    public void SetLanguage(int langIndex)
    {
        if (langIndex < 0 || langIndex >= Strings.Culture.Length)
            return;

        // 使用 languageOverride 参数确保 InitConfig 从文件重载 Config 后不会覆盖目标语言。
        _dataManager.TryInit(2, langIndex);
        ApplyCulture(langIndex);
    }

    private static void ApplyCulture(int langIndex)
    {
        var cultureName = Strings.Culture[langIndex];
        var culture = CultureInfo.CreateSpecificCulture(cultureName);

        // 设置 .resx 资源的静态文化（最关键 —— 直接控制 ResourceManager.GetString 的返回语言）
        Xiletrade.Library.Resources.Resources.Culture = culture;

        // 同时设置线程级文化，确保其他依赖 CultureInfo 的代码也正确
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>
    /// 解析剪贴板物品文本，返回 xiletrade 的 FormViewModel（已填充 name/base/rarity/panel/modlist/influence/condition）。
    /// </summary>
    /// <param name="clipboardText">从游戏复制的物品文本。</param>
    /// <param name="showMinMax">是否在词缀行显示 min/max 输入（默认 true 用于查价配置）。</param>
    /// <returns>解析成功返回 FormViewModel；文本不是合法物品返回 null。</returns>
    public FormViewModel? ParseItem(string clipboardText, bool showMinMax = true)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
            return null;

        var infoDesc = new InfoDescription(clipboardText.AsSpan());
        if (!infoDesc.IsPoeItem)
        {
            System.Diagnostics.Debug.WriteLine("[ParseItem] IsPoeItem=false");
            return null;
        }

        // 添加 TraceListener 将 Xiletrade.Library 内部的 Trace.WriteLine 输出转发到 AppLogger
        var traceListener = new AppLoggerTraceListener();
        System.Diagnostics.Trace.Listeners.Add(traceListener);
        try
        {
            var item = new ItemData(_dataManager, infoDesc);

            // 调试日志：追踪解析过程
            AppLogger.Instance.Info($"[ParseItem] Lang={_dataManager.Config.Options.Language}, GameVersion={_dataManager.Config.Options.GameVersion}");
            AppLogger.Instance.Info($"[ParseItem] Item sections={infoDesc.Item.Length}");
            for (int i = 0; i < infoDesc.Item.Length; i++)
            {
                var section = infoDesc.Item[i];
                var preview = section.Length > 120 ? section[..120] + "..." : section;
                AppLogger.Instance.Info($"[ParseItem] Section[{i}]: [{preview.Replace("\r", "\\r").Replace("\n", "\\n")}]");
            }
            AppLogger.Instance.Info($"[ParseItem] Flag.Parseable={item.Flag.Parseable}, Flag.Rare={item.Flag.Rare}, Flag.Boots={item.Flag.Boots}");
            AppLogger.Instance.Info($"[ParseItem] ModList count={item.ModList?.Count ?? -1}");
            AppLogger.Instance.Info($"[ParseItem] Filter.Result count={_dataManager.Filter?.Result?.Length ?? -1}");
            if (_dataManager.Filter?.Result != null)
            {
                foreach (var fr in _dataManager.Filter.Result)
                {
                    AppLogger.Instance.Info($"[ParseItem] Filter label={fr.Label}, entries={fr.Entries?.Length ?? 0}");
                }
            }
            AppLogger.Instance.Info($"[ParseItem] FilterEn.Result count={_dataManager.FilterEn?.Result?.Length ?? -1}");

            var form = new FormViewModel(_serviceProvider, item, infoDesc, showMinMax);
            return form;
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(traceListener);
        }
    }

    /// <summary>将 System.Diagnostics.Trace 输出转发到 AppLogger 的监听器。</summary>
    private sealed class AppLoggerTraceListener : System.Diagnostics.TraceListener
    {
        public override void Write(string message) => AppLogger.Instance.Info(message);
        public override void WriteLine(string message) => AppLogger.Instance.Info(message);
    }

    // ── 存根实现：仅在 DataManagerService.TryInit 出错时被调用 ──

    private sealed class StubMessageAdapter : IMessageAdapterService
    {
        public void Show(string message, string caption, MessageStatus status)
        {
            // 不弹窗，仅记录到日志（避免在桥接层引入 UI 依赖）
            System.Diagnostics.Debug.WriteLine($"[Xiletrade] {caption}: {message}");
        }

        public bool ShowResult(string message, string caption, MessageStatus status, bool yesNo = false)
        {
            System.Diagnostics.Debug.WriteLine($"[Xiletrade] {caption}: {message}");
            return true;
        }

        public async Task<bool> ShowResultAsync(string message, string caption, MessageStatus status, bool yesNo = false)
        {
            System.Diagnostics.Debug.WriteLine($"[Xiletrade] {caption}: {message}");
            await Task.CompletedTask;
            return true;
        }
    }

    private sealed class StubNavigationService : INavigationService
    {
        public void InstantiateMainView() { }
        public void ShowMainView() { }
        public bool IsVisibleMainView() => false;
        public void CloseMainView() { }
        public void ShowConfigView() { }
        public void ShowEditorView() { }
        public void ShowRegexView() { }
        public void ShowPopupView(string imgName) { }
        public Task ShowStartView() => Task.CompletedTask;
        public void ShowUpdateView(Xiletrade.Library.Models.GitHub.Contract.GitHubRelease release) { }
        public void ShowWhisperView(Tuple<Xiletrade.Library.Models.Poe.Contract.FetchDataListing, Xiletrade.Library.Models.Poe.Contract.OfferInfo> data) { }
        public void SetMainHandle(object view) { }
        public void DelegateActionToUiThread(Action action) => action();
        public TResult DelegateFuncToUiThread<TResult>(Func<TResult> func) => func();
        public Task<TResult> DelegateActionToUiThreadAsync<TResult>(Func<Task<TResult>> asyncFunc) => asyncFunc();
        public void ShutDownXiletrade(int code = 0) { }
        public string GetKeyPressed(EventArgs e) => string.Empty;
        public int GetModifierCode(string textMod) => 0;
        public string GetModifierText(int modifier) => string.Empty;
        public void ClearKeyboardFocus() { }
    }
}
