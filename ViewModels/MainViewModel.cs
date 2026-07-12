using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Poe2PriceGui.Models;
using Poe2PriceGui.Services;
using Poe2PriceGui.Services.Smoother;
using Poe2PriceGui.Windows;
using Xiletrade.Library.Shared;
using Xiletrade.Library.Shared.Enum;
using Xiletrade.Library.ViewModels.Main.Form;

namespace Poe2PriceGui.ViewModels;

/// <summary>
/// 主窗口 ViewModel：绑定价格表格、刷新与保存命令。
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly IconCacheService _iconCacheService;
    private readonly PriceDataService _priceDataService;
    private readonly ToastService _toastService;
    private readonly SettingsService _settingsService;
    private readonly PatchExportService _patchExportService;
    private readonly PatchInstaller _patchInstaller;
    private readonly HttpClient _httpClient;
    private readonly UpdateService _updateService;
    private AppSettings _settings;
    private CancellationTokenSource? _autoSaveDebounceCts;
    private ObservableCollection<PoecurrencyItem> _prices = [];
    private string _statusMessage = "就绪";
    private string _lastRefreshTime = "无";
    private bool _isBusy;
    private int _editedCount;
    private ObservableCollection<string> _categories = [];
    private string _selectedCategory = "全部";
    private string _searchText = "";
    private string _cacheStatusMessage = "";
    private string _settingsStatusMessage = "";
    private bool _priceCheckerEnabled;
    private string _priceCheckerHotkey = "Ctrl+D";
    private string _priceCheckerPoeSessionId = "";
    private string _priceCheckerIntlPoeSessionId = "";
    private string _priceCheckerLeague = "";
    private int _priceCheckerLanguage = 9;
    private ObservableCollection<string> _availableLeagues = new();
    private ObservableCollection<string> _availableLanguages = new()
    {
        "英文 (en-US)",
        "韩文 (ko-KR)",
        "法文 (fr-FR)",
        "西班牙文 (es-ES)",
        "德文 (de-DE)",
        "葡萄牙文 (pt-BR)",
        "俄文 (ru-RU)",
        "泰文 (th-TH)",
        "繁体中文 (zh-TW)",
        "简体中文 (zh-CN)",
        "日文 (ja-JP)",
    };
    private string _currencyPriceToken = "789486ce3baf2c4a7e18f4ba0b9aa4ab8edb9da64ca92bca10ca74c094cd8f8d";
    private ListCollectionView _filteredPrices = new(new ObservableCollection<PoecurrencyItem>());
    private PriceOverlayWindow? _currentOverlay;

    /// <summary>技能特效补丁文件列表（修改用）。</summary>
    private ObservableCollection<string> _skillPatchFiles = [];
    /// <summary>技能特效还原文件列表。</summary>
    private ObservableCollection<string> _skillRestoreFiles = [];
    /// <summary>当前选中的技能特效补丁。</summary>
    private string _selectedSkillPatch = "";
    /// <summary>当前选中的技能特效还原文件。</summary>
    private string _selectedSkillRestore = "";
    /// <summary>技能特效补丁操作状态提示。</summary>
    private string _skillPatchStatus = "";

    /// <summary>当前价格服务（国服/国际服切换）。</summary>
    private IPriceService _priceService = null!;
    /// <summary>交易搜索服务（国服/国际服切换）。</summary>
    private PoeTradeService _tradeService = null!;
    /// <summary>当前是否为国服。</summary>
    private bool _isChinaServer = true;
    /// <summary>价格页数据来源说明。</summary>
    private string _priceDataSourceLabel = "poecurrency.top (国服)";
    /// <summary>国际服物品名翻译器（英文名→游戏语言名）。</summary>
    private ItemNameTranslator? _itemNameTranslator;
    /// <summary>翻译器加载任务，确保价格刷新前翻译表已就绪。</summary>
    private Task? _translationLoadTask;

    public MainViewModel()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Poe2PriceGui/1.0");
        VmTrace("HttpClient 构造后");
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();
        VmTrace("Settings 加载后");
        _priceCheckerEnabled = _settings.PriceCheckerEnabled;
        _priceCheckerHotkey = _settings.PriceCheckerHotkey;
        _priceCheckerPoeSessionId = _settings.PriceCheckerPoeSessionId;
        _priceCheckerIntlPoeSessionId = _settings.PriceCheckerIntlPoeSessionId;
        _priceCheckerLeague = _settings.PriceCheckerLeague;
        _priceCheckerLanguage = _settings.PriceCheckerLanguage;
        _currencyPriceToken = _settings.CurrencyPriceToken;
        _intlFallbackEnabled = _settings.IntlFallbackEnabled;

        // 先根据已保存的游戏目录检测区服，再创建对应的价格/交易服务。
        VmTrace("RefreshDetectedGameMode 前");
        RefreshDetectedGameMode();
        VmTrace("RebuildPriceAndTradeServices 前");
        RebuildPriceAndTradeServices();
        VmTrace("RebuildPriceAndTradeServices 后");

        // 异步获取赛季列表，校正当前赛季名（不阻塞构造函数）。
        _ = Task.Run(async () => await ValidateLeagueAsync());

        _iconCacheService = new IconCacheService(_httpClient);
        _priceDataService = new PriceDataService();
        _toastService = new ToastService();
        _patchExportService = new PatchExportService();
        _patchInstaller = new PatchInstaller(_patchExportService);
        _updateService = new UpdateService();
        VmTrace("Services 构造后");
        RefreshLastRefreshTimeDisplay();

        RefreshCommand = new RelayCommand(async () => await RefreshPricesAsync(), () => !IsBusy);
        CleanCacheCommand = new RelayCommand(CleanCache, () => !IsBusy);
        OpenLogCommand = new RelayCommand(OpenLogFile, () => File.Exists(AppLogger.Instance.LogFilePath));
        CleanLogCommand = new RelayCommand(CleanLogs, () => Directory.Exists(AppLogger.Instance.LogDirectory) && Directory.GetFiles(AppLogger.Instance.LogDirectory, "*.log").Length > 0);
        ExportStatsCacheCommand = new RelayCommand(async () => await ExportStatsCacheAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(EffectivePoeSessionId));
        ExportPricesCommand = new RelayCommand(async () => await ExportPricesAsync(), () => Prices.Count > 0);
        ExportPatchCommand = new RelayCommand(async () => await ExportPatchAsync(), () => Prices.Count > 0);
        InstallPatchCommand = new RelayCommand(async () => await InstallPatchAsync(), () => Prices.Count > 0);
        RestoreBackupCommand = new RelayCommand(async () => await RestoreBackupAsync(), () => !IsBusy);
        ClearBackupsCommand = new RelayCommand(async () => await ClearBackupsAsync(), () => !IsBusy);
        AutoDetectGameDirectoryCommand = new RelayCommand(ShowAutoDetectGameDirectory, () => !IsBusy);
        OpenPriceCheckerLoginCommand = new RelayCommand(OpenPriceCheckerLoginBrowser);
        CaptureHotkeyCommand = new RelayCommand(CaptureHotkey);
        TestPriceCheckerCommand = new RelayCommand(async () => await TestPriceCheckerAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(EffectivePoeSessionId));
        TestPriceCheckerQuiverCommand = new RelayCommand(async () => await TestPriceCheckerAsync("quiver"), () => !IsBusy && !string.IsNullOrWhiteSpace(EffectivePoeSessionId));
        TestPriceCheckerSpearCommand = new RelayCommand(async () => await TestPriceCheckerAsync("spear"), () => !IsBusy && !string.IsNullOrWhiteSpace(EffectivePoeSessionId));
        TestPriceCheckerCharmCommand = new RelayCommand(async () => await TestPriceCheckerAsync("charm"), () => !IsBusy && !string.IsNullOrWhiteSpace(EffectivePoeSessionId));
        TestPriceCheckerArmourCommand = new RelayCommand(async () => await TestPriceCheckerAsync("armour"), () => !IsBusy && !string.IsNullOrWhiteSpace(EffectivePoeSessionId));
        TestPriceCheckerIntlCommand = new RelayCommand(async () => await TestPriceCheckerAsync("intl-shoes"), () => !IsBusy && !string.IsNullOrWhiteSpace(EffectivePoeSessionId));
        CheckForUpdateCommand = new RelayCommand(async () => await CheckForUpdateAsync(), () => !IsBusy);
        ForceSwitchServerCommand = new RelayCommand(ForceSwitchServer, () => !IsBusy);

        // 泥人补丁命令
        SmootherApplyCommand = new RelayCommand(async () => await SmootherApplyAsync(), () => !IsBusy);
        SmootherPreviewCommand = new RelayCommand(async () => await SmootherPreviewAsync(), () => !IsBusy);
        SmootherRestoreCommand = new RelayCommand(async () => await SmootherRestoreAsync(), () => !IsBusy);
        SmootherCheckCommand = new RelayCommand(async () => await SmootherCheckAsync(), () => !IsBusy);
        SmootherSelectAllCommand = new RelayCommand(SmootherSelectAll, () => !IsBusy);
        SmootherSelectNoneCommand = new RelayCommand(SmootherSelectNone, () => !IsBusy);
        SmootherApplyPresetCommand = new RelayCommand<string>(SmootherApplyPreset, _ => !IsBusy);

        // 技能特效补丁命令
        ApplySkillPatchCommand = new RelayCommand(async () => await ApplySkillPatchAsync(isRestore: false), () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedSkillPatch));
        ApplySkillRestoreCommand = new RelayCommand(async () => await ApplySkillPatchAsync(isRestore: true), () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedSkillRestore));
        OpenSkillPatchDirectoryCommand = new RelayCommand(OpenSkillPatchDirectory);
        OpenSkillRestoreDirectoryCommand = new RelayCommand(OpenSkillRestoreDirectory);
        RefreshSkillPatchFilesCommand = new RelayCommand(LoadSkillPatchFiles);

        // 设置-目录命令
        OpenAppDataDirectoryCommand = new RelayCommand(OpenAppDataDirectory);
        OpenProgramDataDirectoryCommand = new RelayCommand(OpenProgramDataDirectory);

        // 生成翻译表命令（开发者用：从游戏 datc64 构建英文名→中文名映射表）
        GenerateTranslationsCommand = new RelayCommand(async () => await GenerateTranslationsAsync("zh-CN"), () => !IsBusy && !string.IsNullOrWhiteSpace(GameDirectory));
        GenerateTranslationsTradCommand = new RelayCommand(async () => await GenerateTranslationsAsync("zh-TW"), () => !IsBusy && !string.IsNullOrWhiteSpace(GameDirectory));

        _filteredPrices.Filter = FilterBySelectedCategory;

        // 初始化泥人补丁勾选状态（从 settings 读取已保存的勾选列表）。
        InitSmootherPatchChecked();
        // 同步读取已保存的 zoom 值。
        _smootherCameraZoom = _settings.SmootherCameraZoom > 0 ? _settings.SmootherCameraZoom : 2.4;

        // 加载技能特效补丁文件列表（程序目录 data + AppDataPath 对应目录）。
        LoadSkillPatchFiles();

        // 启动时优先加载本地数据。
        _ = LoadLocalPricesAsync();
        VmTrace("MainViewModel 构造完成");
    }

    /// <summary>
    /// 启动追踪：写到 startup.log，帮助定位闪退点。
    /// </summary>
    private static void VmTrace(string step)
    {
        try
        {
            var logDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Poe2PriceGuiData", "logs");
            System.IO.Directory.CreateDirectory(logDir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(logDir, "startup.log"),
                $"[{System.DateTime.Now:HH:mm:ss.fff}] [ViewModel] {step}\r\n",
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // 追踪日志失败不影响启动。
        }
    }

    /// <summary>原始价格列表。</summary>
    public ObservableCollection<PoecurrencyItem> Prices
    {
        get => _prices;
        private set
        {
            if (SetProperty(ref _prices, value))
            {
                RefreshCategoriesAndFilter();
            }
        }
    }

    /// <summary>按当前选中分类过滤后的价格视图，绑定到 DataGrid。</summary>
    public ICollectionView FilteredPrices => _filteredPrices;

    /// <summary>分类页签列表，末尾包含"全部"。</summary>
    public ObservableCollection<string> Categories
    {
        get => _categories;
        private set => SetProperty(ref _categories, value);
    }

    /// <summary>当前选中的分类页签。</summary>
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(IsAllCategorySelected));
                _filteredPrices.Refresh();
                StatusMessage = $"当前分类：{value}，共 {_filteredPrices.Count} 条";
            }
        }
    }

    /// <summary>当前是否为"全部"分类。</summary>
    public bool IsAllCategorySelected => SelectedCategory == "全部";

    /// <summary>物品搜索文本，仅在"全部"分类下生效。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _filteredPrices.Refresh();
            }
        }
    }

    /// <summary>底部状态栏文本。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>上次成功刷新价格的本地时间显示。</summary>
    public string LastRefreshTime
    {
        get => _lastRefreshTime;
        set => SetProperty(ref _lastRefreshTime, value);
    }

    /// <summary>是否正在执行后台操作。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>用户已编辑的价格条目数量。</summary>
    public int EditedCount
    {
        get => _editedCount;
        set => SetProperty(ref _editedCount, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand CleanCacheCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand CleanLogCommand { get; }
    public ICommand ExportStatsCacheCommand { get; }
    public ICommand ExportPricesCommand { get; }
    public ICommand ExportPatchCommand { get; }
    public ICommand InstallPatchCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand ClearBackupsCommand { get; }
    public ICommand AutoDetectGameDirectoryCommand { get; }
    public ICommand OpenPriceCheckerLoginCommand { get; }
    public ICommand CaptureHotkeyCommand { get; }
    public ICommand TestPriceCheckerCommand { get; }
    public ICommand TestPriceCheckerQuiverCommand { get; }
    public ICommand TestPriceCheckerSpearCommand { get; }
    public ICommand TestPriceCheckerCharmCommand { get; }
    public ICommand TestPriceCheckerArmourCommand { get; }
    public ICommand TestPriceCheckerIntlCommand { get; }
    public ICommand CheckForUpdateCommand { get; }
    public ICommand ForceSwitchServerCommand { get; }

    // 设置-目录命令
    public ICommand OpenAppDataDirectoryCommand { get; }
    public ICommand OpenProgramDataDirectoryCommand { get; }

    // 泥人补丁命令
    public ICommand SmootherApplyCommand { get; }
    public ICommand SmootherPreviewCommand { get; }
    public ICommand SmootherRestoreCommand { get; }
    public ICommand SmootherCheckCommand { get; }
    public ICommand SmootherSelectAllCommand { get; }
    public ICommand SmootherSelectNoneCommand { get; }
    public RelayCommand<string> SmootherApplyPresetCommand { get; }

    // 技能特效补丁命令
    public ICommand ApplySkillPatchCommand { get; }
    public ICommand ApplySkillRestoreCommand { get; }
    public ICommand OpenSkillPatchDirectoryCommand { get; }
    public ICommand OpenSkillRestoreDirectoryCommand { get; }
    public ICommand RefreshSkillPatchFilesCommand { get; }

    /// <summary>生成翻译表命令（开发者用）：从游戏 datc64 构建英文名→中文名映射表并保存到 data/translations/。</summary>
    public ICommand GenerateTranslationsCommand { get; }

    /// <summary>生成繁体翻译表命令（开发者用）：从游戏 datc64 构建英文名→繁中名映射表并保存到 data/translations/。</summary>
    public ICommand GenerateTranslationsTradCommand { get; }

    /// <summary>
    /// 状态栏消息。设置页缓存清理状态文本。
    /// </summary>
    public string CacheStatusMessage
    {
        get => _cacheStatusMessage;
        set => SetProperty(ref _cacheStatusMessage, value);
    }

    /// <summary>
    /// Toast 通知列表，绑定到右上角提示面板。
    /// </summary>
    public ObservableCollection<ToastNotification> Toasts => _toastService.Toasts;

    /// <summary>
    /// 当前日志文件路径，显示在设置页。
    /// </summary>
    public string LogFilePath => AppLogger.Instance.LogFilePath;

    /// <summary>
    /// POE2 游戏根目录。
    /// </summary>
    public string GameDirectory
    {
        get => _settings.GameDirectory;
        set
        {
            if (_settings.GameDirectory != value)
            {
                _settings.GameDirectory = value;
                _settingsService.Save(_settings);
                OnPropertyChanged();
                RefreshDetectedGameMode();
                ((RelayCommand)InstallPatchCommand).RaiseCanExecuteChanged();
                ((RelayCommand)GenerateTranslationsCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private string _detectedGameMode = "未检测";

    /// <summary>
    /// 根据 GameDirectory 自动检测到的游戏版本。
    /// </summary>
    public string DetectedGameMode
    {
        get => _detectedGameMode;
        set => SetProperty(ref _detectedGameMode, value);
    }

    /// <summary>
    /// 价格页标题，例如 "POE2 国服价格" 或 "POE2 国际服价格"。
    /// </summary>
    public string PricePageTitle => _isChinaServer ? "POE2 国服价格" : "POE2 国际服价格";

    /// <summary>
    /// 价格页数据来源说明，例如 "poecurrency.top (国服)"。
    /// </summary>
    public string PriceDataSourceLabel
    {
        get => _priceDataSourceLabel;
        set
        {
            if (SetProperty(ref _priceDataSourceLabel, value))
            {
                OnPropertyChanged(nameof(PricePageTitle));
            }
        }
    }

    /// <summary>当前是否为国服。</summary>
    public bool IsChinaServer => _isChinaServer;

    /// <summary>查价器是否启用。</summary>
    public bool PriceCheckerEnabled
    {
        get => _priceCheckerEnabled;
        set
        {
            if (SetProperty(ref _priceCheckerEnabled, value))
            {
                _settings.PriceCheckerEnabled = value;
                _settingsService.Save(_settings);
                PriceCheckerSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>查价器热键文本，例如 "Ctrl+D"。</summary>
    public string PriceCheckerHotkey
    {
        get => _priceCheckerHotkey;
        set
        {
            if (SetProperty(ref _priceCheckerHotkey, value))
            {
                _settings.PriceCheckerHotkey = value;
                _settingsService.Save(_settings);
                PriceCheckerSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>查价器国服 POESESSID（仅国服模式下使用与保存）。</summary>
    public string PriceCheckerPoeSessionId
    {
        get => _priceCheckerPoeSessionId;
        set
        {
            if (SetProperty(ref _priceCheckerPoeSessionId, value))
            {
                _settings.PriceCheckerPoeSessionId = value;
                _settingsService.Save(_settings);
                OnPoeSessionIdChanged();
            }
        }
    }

    /// <summary>查价器国际服 POESESSID（仅国际服模式下使用与保存，与国服分离）。</summary>
    public string PriceCheckerIntlPoeSessionId
    {
        get => _priceCheckerIntlPoeSessionId;
        set
        {
            if (SetProperty(ref _priceCheckerIntlPoeSessionId, value))
            {
                _settings.PriceCheckerIntlPoeSessionId = value;
                _settingsService.Save(_settings);
                OnPoeSessionIdChanged();
            }
        }
    }

    /// <summary>
    /// 当前区服生效的 POESESSID：国服返回国服字段，国际服返回国际服字段。
    /// 所有查价/测试/导出等命令均应使用此属性，避免取错区服 Cookie。
    /// </summary>
    public string EffectivePoeSessionId => _isChinaServer
        ? _priceCheckerPoeSessionId
        : _priceCheckerIntlPoeSessionId;

    /// <summary>
    /// 会话 ID 变更或区服切换时的统一通知：刷新命令可用性、登录状态、预加载 stats。
    /// 命令在构造函数早期可能尚未创建，做 null 保护。
    /// </summary>
    private void OnPoeSessionIdChanged()
    {
        if (ExportStatsCacheCommand is RelayCommand exportCmd) exportCmd.RaiseCanExecuteChanged();
        if (TestPriceCheckerCommand is RelayCommand testCmd) testCmd.RaiseCanExecuteChanged();
        if (TestPriceCheckerQuiverCommand is RelayCommand quiverCmd) quiverCmd.RaiseCanExecuteChanged();
        if (TestPriceCheckerSpearCommand is RelayCommand spearCmd) spearCmd.RaiseCanExecuteChanged();
        if (TestPriceCheckerCharmCommand is RelayCommand charmCmd) charmCmd.RaiseCanExecuteChanged();
        if (TestPriceCheckerArmourCommand is RelayCommand armourCmd) armourCmd.RaiseCanExecuteChanged();
        if (TestPriceCheckerIntlCommand is RelayCommand intlCmd) intlCmd.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(EffectivePoeSessionId));
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(LoginStatusText));
        OnPropertyChanged(nameof(LoginStatusColor));

        // 后台预加载 stats 数据，避免首次查价时阻塞。
        // 参考 xiletrade-master 启动时同步加载所有静态数据到内存。
        var sid = EffectivePoeSessionId;
        if (!string.IsNullOrWhiteSpace(sid) && _tradeService != null)
        {
            _ = _tradeService.PreloadStatsAsync(sid);
        }
    }

    /// <summary>是否已登录（当前区服的 POESESSID 非空）。</summary>
    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(EffectivePoeSessionId);

    /// <summary>登录状态文本。</summary>
    public string LoginStatusText => IsLoggedIn ? "已登录" : "未登录";

    /// <summary>登录状态颜色。</summary>
    public System.Windows.Media.Brush LoginStatusColor => IsLoggedIn
        ? System.Windows.Media.Brushes.DarkGreen
        : System.Windows.Media.Brushes.Gray;

    /// <summary>查价器目标赛季。</summary>
    public string PriceCheckerLeague
    {
        get => _priceCheckerLeague;
        set
        {
            if (SetProperty(ref _priceCheckerLeague, value))
            {
                _settings.PriceCheckerLeague = value;
                _settingsService.Save(_settings);
            }
        }
    }

    /// <summary>
    /// 可用赛季列表（从 API 动态获取），绑定到设置页下拉框。
    /// </summary>
    public ObservableCollection<string> AvailableLeagues
    {
        get => _availableLeagues;
        private set => SetProperty(ref _availableLeagues, value);
    }

    /// <summary>
    /// 查价器解析语言索引（对应 xiletrade Strings.Culture 数组：0=en-US, 9=zh-CN, 10=ja-JP, ...）。
    /// 切换时即时应用到 XiletradePriceService。
    /// </summary>
    public int PriceCheckerLanguage
    {
        get => _priceCheckerLanguage;
        set
        {
            if (SetProperty(ref _priceCheckerLanguage, value))
            {
                _settings.PriceCheckerLanguage = value;
                _settingsService.Save(_settings);
                XiletradePriceService.Instance.SetLanguage(value);
                AppLogger.Instance.Info($"查价器语言切换：index={value}, culture={Xiletrade.Library.Shared.Strings.Culture[value]}");
            }
        }
    }

    /// <summary>
    /// 可用语言列表，绑定到设置页下拉框。
    /// </summary>
    public ObservableCollection<string> AvailableLanguages => _availableLanguages;

    /// <summary>通货价格查询 Token，为空时使用公共接口，非空时使用 summary_validate 接口。</summary>
    public string CurrencyPriceToken
    {
        get => _currencyPriceToken;
        set
        {
            if (SetProperty(ref _currencyPriceToken, value))
            {
                _settings.CurrencyPriceToken = value;
                _settingsService.Save(_settings);
                // 国服价格服务需要同步 Token。
                if (_priceService is PoecurrencyPriceService cn)
                {
                    cn.Token = value;
                }
            }
        }
    }

    /// <summary>技能特效补丁文件列表（修改用）。</summary>
    public ObservableCollection<string> SkillPatchFiles => _skillPatchFiles;

    /// <summary>技能特效还原文件列表。</summary>
    public ObservableCollection<string> SkillRestoreFiles => _skillRestoreFiles;

    /// <summary>当前选中的技能特效补丁。</summary>
    public string SelectedSkillPatch
    {
        get => _selectedSkillPatch;
        set
        {
            if (SetProperty(ref _selectedSkillPatch, value))
            {
                (ApplySkillPatchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>当前选中的技能特效还原文件。</summary>
    public string SelectedSkillRestore
    {
        get => _selectedSkillRestore;
        set
        {
            if (SetProperty(ref _selectedSkillRestore, value))
            {
                (ApplySkillRestoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>技能特效补丁操作状态提示。</summary>
    public string SkillPatchStatus
    {
        get => _skillPatchStatus;
        set => SetProperty(ref _skillPatchStatus, value);
    }

    private bool _intlFallbackEnabled;

    /// <summary>
    /// 国服模式下，是否启用"没有的道具国际服兜底"。
    /// 开启后刷新价格时会并行获取国际服 poe2scout 数据，翻译后补充国服未覆盖的物品。
    /// 默认关闭。仅国服模式可见。
    /// </summary>
    public bool IntlFallbackEnabled
    {
        get => _intlFallbackEnabled;
        set
        {
            if (SetProperty(ref _intlFallbackEnabled, value))
            {
                _settings.IntlFallbackEnabled = value;
                _settingsService.Save(_settings);
            }
        }
    }

    /// <summary>查价器开关/热键变更通知。</summary>
    public event EventHandler? PriceCheckerSettingsChanged;

    /// <summary>
    /// 执行查价器：读取剪贴板装备文本，解析后显示配置叠加层（不直接搜索）。
    /// 保证同一时刻只有一个叠加层。
    /// </summary>
    public async Task RunPriceCheckerAsync()
    {
        AppLogger.Instance.Info($"查价器触发，Enabled={PriceCheckerEnabled}, IsBusy={IsBusy}");

        if (!PriceCheckerEnabled || IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PriceCheckerLeague))
        {
            _toastService.ShowWarning("请先在设置页配置查价器目标赛季");
            return;
        }

        IsBusy = true;
        try
        {
            // 如果叠加层正在显示，先关闭它，让用户能再次复制新装备。
            // 此时焦点可能还在 overlay 上，后续 CopyItemTextFromGame 会尝试切回游戏窗口。
            if (_currentOverlay != null)
            {
                try
                {
                    _currentOverlay.Close();
                }
                catch { /* 忽略关闭异常 */ }
                _currentOverlay = null;
            }

            var itemText = ClipboardService.CopyItemTextFromGame();
            AppLogger.Instance.Info($"剪贴板获取文本长度：{itemText?.Length ?? 0}");
            if (string.IsNullOrWhiteSpace(itemText))
            {
                ShowOverlayError("未获取到装备信息", "请确保鼠标悬停在装备上后再按热键");
                return;
            }

            // 使用 xiletrade 的解析管线：InfoDescription → ItemData → FormViewModel
            FormViewModel? form;
            try
            {
                EnsurePriceCheckerLanguage();
                form = XiletradePriceService.Instance.ParseItem(itemText);
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, "xiletrade 解析异常");
                ShowOverlayError("无法解析装备信息", ex.Message);
                return;
            }

            if (form == null)
            {
                ShowOverlayError("无法解析装备信息", "剪贴板内容不是有效的装备文本，请确保游戏窗口处于前台且鼠标悬停在装备上");
                AppLogger.Instance.Warn($"剪贴板内容前 100 字符：{itemText[..Math.Min(100, itemText.Length)]}");
                return;
            }

            AppLogger.Instance.Info($"解析结果：Name={form.ItemName}, BaseType={form.ItemBaseType}, ByBase={form.ByBase}, IsPoeTwo={form.IsPoeTwo}");

            var viewModel = new PriceOverlayViewModel
            {
                Form = form,
                SearchCallback = ExecuteOverlaySearchAsync,
            };

            ShowOverlay(viewModel);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "查价器执行失败");
            ShowOverlayError("查价失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 测试查价器：使用内置的猎首腰带文本模拟查价流程，不依赖剪贴板。
    /// </summary>
    public async Task TestPriceCheckerAsync(string? testItem = null)
    {
        AppLogger.Instance.Info($"测试查价器触发{(testItem != null ? $"（{testItem}）" : "")}");

        if (string.IsNullOrWhiteSpace(PriceCheckerLeague))
        {
            _toastService.ShowWarning("请先在设置页配置查价器目标赛季");
            return;
        }

        IsBusy = true;
        try
        {
            string itemText;
            string testLabel;

            if (testItem == "quiver")
            {
                testLabel = "精工箭袋";
                itemText = @"物品类别: 箭袋
稀有度: 传奇
精工箭袋
--------
物品等级: 80
--------
{ 基底属性 — 攻击, 速度 }
攻击速度提高 7 (7-10)%
--------
未鉴定
--------
只能在使用弓时装备。
--------
引路石掉落";
            }
            else if (testItem == "spear")
            {
                testLabel = "苍穹裂片";
                itemText = @"物品类别: 战矛
稀有度: 传奇
苍穹裂片
飞翼长矛
--------
闪电伤害: 1 或 91 (lightning)
暴击率: 5.00%
每秒攻击次数: 2.36 (augmented)
--------
需求： 等级 16, 12 力量, 25 敏捷
--------
插槽: S
--------
物品等级: 79
--------
攻击速度提高 5% (rune)
--------
获得技能: 战矛飞掷
--------
{ 传奇属性 — 伤害, 物理, 攻击 }
没有物理伤害
{ 传奇属性 — 伤害, 元素, 闪电, 攻击 }
附加 1 - 91 (80-120) 闪电伤害
{ 传奇属性 — 攻击, 速度 }
攻击速度提高 34 (15-30)%
{ 传奇属性 — 元素, 闪电, 异常状态 }
感电几率提高 57 (50-100)%
{ 传奇属性 — 伤害 }
每一种伤害类型只能重置伤害的下限或上限 — 数值不可调整
--------
首级滚落杀害，星辰坠落天际。
--------
被腐化";
            }
            else if (testItem == "charm")
            {
                testLabel = "仪式通道";
                itemText = @"物品类别: 咒符
稀有度: 传奇
仪式通道
黄金咒符
--------
持续 1 秒
每次使用会从 80 充能次数中消耗 80 次
目前有 40 充能次数
物品稀有度提高 15%
--------
需求： 等级 50
--------
物品等级: 81
--------
{ 基底属性 }
当你击败稀有或传奇敌人时使用 — 数值不可调整
--------
{ 传奇属性 — 咒符 }
使用时被豹之灵附身 19 (10-20) 秒
--------
要想成为战士和猎手，每个年轻的
阿兹莫里人都必须在万灵面前证明自己。
--------
满足条件时自动使用。只有装备于腰带上时才会充能。可通过水井或击败怪物补充。
--------
引路石掉落";
            }
            else if (testItem == "armour")
            {
                testLabel = "祸害 魔甲";
                itemText = @"物品类别: 护甲
稀有度: 稀有
祸害 魔甲
滑击背心
--------
品质: +20% (augmented)
闪避值: 2673 (augmented)
--------
需求： 等级 70, 121 敏捷
--------
插槽: S S
--------
物品等级: 81
--------
护甲、闪避和能量护盾提高 40% (rune)
--------
{ 前缀属性 ""易变的"" (等阶：1) — 闪避 }
+294 (262-300) 点闪避值
{ 亵渎的 前缀属性 ""公羊的"" (等阶：3) — 生命, 闪避 }
闪避值提高 29 (27-32)%
+32 (26-32) 生命上限
{ 前缀属性 ""幻迷的"" (等阶：1) — 闪避 }
闪避值提高 105 (101-110)%
{ 后缀属性 ""岩浆之"" (等阶：2) — 元素, 火焰, 抗性 }
火焰抗性 +37 (36-40)%
{ 后缀属性 ""挠曲之"" (等阶：2) — 闪避 }
获得相当于闪避值 22 (21-23)% 的偏转值
{ 打造的 后缀属性 ""台风之"" (等阶：3) — 元素, 闪电, 抗性 }
闪电抗性 +35 (31-35)%
--------
引路石掉落";
            }
            else if (testItem == "intl-shoes")
            {
                testLabel = "惡魔 套靴（国际服）";
                itemText = @"物品種類: 鞋子
稀有度: 稀有
惡魔 套靴
羽毛便鞋
--------
能量護盾: 52 (augmented)
--------
需求: 等級 40, 45 智慧
--------
物品等級: 50
--------
{ 前綴 ""乳白色的"" (階層：3) — 魔力 }
+86 (80-89) 最大魔力
{ 前綴 ""瞪羚的"" (階層：3) — 速度 }
增加25%移動速度
{ 前綴 ""納迦的"" (階層：3) }
增加 27 (27-32)% 最大能量護盾
暈眩門檻 +59 (41-63)
{ 後綴 ""掃蕩之"" (階層：2) — 丟置 }
增加11 (11-14)%找到的物品稀有度
{ 後綴 ""海象之"" (階層：4) — 元素,冰冷,抗性 }
+30 (26-30)% 冰冷抗性
{ 後綴 ""暴風雨之"" (階層：4) — 元素,閃電,抗性 }
+29 (26-30)% 閃電抗性";
            }
            else
            {
                testLabel = "猎首";
                itemText = @"物品类别: 腰带
稀有度: 传奇
猎首
重革腰带
--------
需求： 等级 50
--------
物品等级: 81
--------
{ 基底属性 }
晕眩阈值提高 23 (20-30)%
{ 基底属性 — 咒符 }
具有 2 (1-3) 个咒符位
--------
{ 传奇属性 — 属性 }
+35 (20-40) 力量
{ 传奇属性 — 属性 }
+22 (20-40) 敏捷
{ 传奇属性 — 生命 }
+59 (40-60) 生命上限
{ 传奇属性 }
当你击败稀有怪物时，获得它的词缀，持续 60 秒
--------
" + "\"骨骼是灵魂的居所，\n血肉是精神和世界交流的窗口，推动一切的力量就在心窝。\n即使有了这些，失去了头脑就没有自我。\"\n——冈姆军师拉维安加\n--------\n引路石掉落";
            }

            // 国际服测试物品为繁体中文客户端文本，必须临时切换到繁体中文（zh-TW）才能正确解析词缀。
            var restoreLang = -1;
            try
            {
                if (testItem == "intl-shoes")
                {
                    var svc = XiletradePriceService.Instance;
                    restoreLang = svc.CurrentLanguage;
                    var zhTwIndex = Array.IndexOf(Strings.Culture, "zh-TW");
                    if (restoreLang != zhTwIndex)
                    {
                        svc.SetLanguage(zhTwIndex);
                        AppLogger.Instance.Info("国际服测试：临时切换解析语言到繁体中文（zh-TW）");
                    }
                }
                else
                {
                    EnsurePriceCheckerLanguage();
                }

                var form = XiletradePriceService.Instance.ParseItem(itemText);
                AppLogger.Instance.Info($"测试解析结果：Name={form?.ItemName}, BaseType={form?.ItemBaseType}, ByBase={form?.ByBase}, Mods={form?.ModList?.Count ?? 0}");
                if (form == null)
                {
                    _toastService.ShowError("测试文本解析失败");
                    return;
                }

                var viewModel = new PriceOverlayViewModel
                {
                    Form = form,
                    SearchCallback = ExecuteOverlaySearchAsync,
                };

                ShowOverlay(viewModel);
                _toastService.ShowInfo($"已加载测试物品（{testLabel}），可在叠加层中点击搜索");
            }
            finally
            {
                if (restoreLang >= 0 && restoreLang != XiletradePriceService.Instance.CurrentLanguage)
                {
                    XiletradePriceService.Instance.SetLanguage(restoreLang);
                    AppLogger.Instance.Info($"国际服测试：恢复解析语言到索引 {restoreLang}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "测试查价器执行失败");
            _toastService.ShowError($"测试失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 叠加层搜索回调：从 FormViewModel 提取搜索条件并执行搜索。
    /// 参考 xiletrade-master GetXiletradeItem：ByBase 决定按 name 还是 type 搜索，
    /// ModList 中 Selected 的词缀作为 stat filter，Rarity/Corrupted/Identified 通过下拉框索引转换。
    /// 搜索执行复用现有 PoeTradeService（过渡方案，后续可切换为 xiletrade 的 JsonDataTwoFactory）。
    /// </summary>
    private async Task ExecuteOverlaySearchAsync(PriceOverlayViewModel vm)
    {
        try
        {
            var form = vm.Form;
            if (form == null)
            {
                vm.ShowError("未解析到物品信息");
                return;
            }

            // 参考 xiletrade JsonDataTwoFactory.Create：
            // - 只有 Unique/FoilVariant 物品按名称搜索（同时传 type）
            // - 其他物品（稀有/魔法/普通等）按基底类型搜索，不应按名称搜索
            // 稀有/魔法装备有随机名称，按名称搜索会返回 400 "Unknown item name"。
            // 国际服交易 API 要求使用英文名称/基底，因此非国服模式优先使用 ItemNameEn/ItemBaseTypeEn。
            var flag = form.ItemFlag;
            var isNamedItem = flag != null && (flag.Unique || flag.FoilVariant)
                              && !string.IsNullOrWhiteSpace(_isChinaServer ? form.ItemName : form.ItemNameEn);
            var searchByType = !isNamedItem;
            var searchTerm = searchByType
                ? (_isChinaServer ? form.ItemBaseType : form.ItemBaseTypeEn)
                : (_isChinaServer ? form.ItemName : form.ItemNameEn);
            var baseTypeValue = _isChinaServer ? form.ItemBaseType : form.ItemBaseTypeEn;

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                vm.ShowError("搜索条件中没有有效的名称或基底");
                return;
            }

            // 收集选中的词缀（参考 xiletrade GetXiletradeItem 遍历 ModList）。
            // Mod.Text → 词缀文本，Mod.Affix[AffixIndex].Type → stat 分类，用于映射到正确的 stat ID。
            // 过滤 TierKind 为空的条目（rune 符文属性 / 固有技能），它们不应作为搜索条件：
            // 1) rune 词缀来自装备插槽符文，并非物品本身属性，POE2 API 不索引此类 stat
            // 2) 国服"攻击速度提高"会被 rune 与物品本身同时解析为两条，rune 那条 TierKind=null
            List<(string Text, string Type, string? Min, string? Max)>? selectedMods = null;
            if (form.ModList?.Count > 0)
            {
                var mods = form.ModList
                    .Where(m => m.Selected && m.Affix.Count > 0 && !string.IsNullOrEmpty(m.TierKind))
                    .Select(m =>
                    {
                        var modText = _isChinaServer ? m.Mod : m.ModEn;
                        if (string.IsNullOrWhiteSpace(modText))
                        {
                            modText = m.Mod;
                        }
                        return (modText, m.Affix[m.AffixIndex].Type,
                            string.IsNullOrWhiteSpace(m.Min) ? null : m.Min,
                            string.IsNullOrWhiteSpace(m.Max) ? null : m.Max);
                    })
                    .ToList();
                if (mods.Count > 0)
                {
                    selectedMods = mods;
                }
            }

            // 稀有度：从 RarityViewModel.ComboBox[Index] 取（参考 xiletrade GetXiletradeItem）。
            string? rarity = null;
            if (form.Rarity != null && form.Rarity.Index >= 0 && form.Rarity.Index < form.Rarity.ComboBox.Count)
            {
                rarity = form.Rarity.ComboBox[form.Rarity.Index];
            }

            // 物品等级：从 Panel.StatList 中找 Id == CommonItemLevel 的条目（参考 xiletrade ApplyStat）。
            // MinMaxViewModel.Min/Max 是 string，Selected 控制是否启用筛选。
            int? itemLevelMin = null;
            int? itemLevelMax = null;
            var ilvlEntry = form.Panel?.StatList?.FirstOrDefault(x => x.Id == StatPanel.CommonItemLevel);
            if (ilvlEntry is { Selected: true })
            {
                if (int.TryParse(ilvlEntry.Min, out var minVal) && minVal > 0)
                    itemLevelMin = minVal;
                if (int.TryParse(ilvlEntry.Max, out var maxVal) && maxVal > 0)
                    itemLevelMax = maxVal;
            }

            // 品质 / 护甲 / 闪避 / 能量护盾：从 Panel.StatList 提取（参考 xiletrade ApplyStat）。
            // 这些是结构化 filter（type_filters.filters.quality / equipment_filters.filters.ar/ev/es），不是 stat filter。
            int? qualityVal = null;
            int? armourVal = null;
            int? evasionVal = null;
            int? energyShieldVal = null;
            var qualityEntry = form.Panel?.StatList?.FirstOrDefault(x => x.Id == StatPanel.CommonQuality);
            if (qualityEntry is { Selected: true } && int.TryParse(qualityEntry.Min, out var qVal) && qVal > 0)
                qualityVal = qVal;
            var armourEntry = form.Panel?.StatList?.FirstOrDefault(x => x.Id == StatPanel.DefenseArmour);
            if (armourEntry is { Selected: true } && int.TryParse(armourEntry.Min, out var arVal) && arVal > 0)
                armourVal = arVal;
            var evasionEntry = form.Panel?.StatList?.FirstOrDefault(x => x.Id == StatPanel.DefenseEvasion);
            if (evasionEntry is { Selected: true } && int.TryParse(evasionEntry.Min, out var evVal) && evVal > 0)
                evasionVal = evVal;
            var esEntry = form.Panel?.StatList?.FirstOrDefault(x => x.Id == StatPanel.DefenseEnergy);
            if (esEntry is { Selected: true } && int.TryParse(esEntry.Min, out var esVal) && esVal > 0)
                energyShieldVal = esVal;

            // 已腐化 / 未鉴定：下拉框索引 0=Any, 1=No, 2=Yes（参考 xiletrade GetOption）。
            bool? corrupted = form.CorruptedIndex switch
            {
                2 => true,
                1 => false,
                _ => null
            };
            bool? identified = form.IdentifiedIndex switch
            {
                1 => false,
                2 => true,
                _ => null
            };

            AppLogger.Instance.Info($"叠加层搜索：league={PriceCheckerLeague}, term={searchTerm}, byType={searchByType}, rarity={rarity}, corrupt={corrupted}, ident={identified}, mods={selectedMods?.Count ?? 0}");

            // 限流等待时通过 IProgress 回调更新叠加层状态文本（如"限流等待中，14 秒后重试..."），
            // 避免用户只看到"搜索中..."却不知道在等限流。Progress<T> 在捕获的同步上下文（UI 线程）上回调。
            var progress = new Progress<string>(msg => vm.StatusMessage = msg);

            TradeSearchResult searchResult;
            try
            {
                searchResult = await _tradeService.SearchAsync(
                    PriceCheckerLeague,
                    searchTerm,
                    EffectivePoeSessionId,
                    searchByType: searchByType,
                    baseType: baseTypeValue,
                    itemLevelMin: itemLevelMin,
                    itemLevelMax: itemLevelMax,
                    rarity: rarity,
                    selectedMods: selectedMods,
                    isExactSearch: false,
                    quality: qualityVal,
                    armour: armourVal,
                    evasion: evasionVal,
                    energyShield: energyShieldVal,
                    corrupted: corrupted,
                    identified: identified,
                    itemFlag: form.ItemFlag,
                    progress: progress);
            }
            catch (HttpRequestException ex) when (!searchByType && ex.Message.Contains("400"))
            {
                // 按名称搜索返回 400（Unknown item name），自动回退按基底类型搜索。
                if (string.IsNullOrWhiteSpace(baseTypeValue)) throw;

                AppLogger.Instance.Info($"按名称搜索返回 400，自动回退按基底类型搜索：{baseTypeValue}");
                searchTerm = baseTypeValue;
                searchByType = true;
                searchResult = await _tradeService.SearchAsync(
                    PriceCheckerLeague,
                    searchTerm,
                    EffectivePoeSessionId,
                    searchByType: searchByType,
                    baseType: baseTypeValue,
                    itemLevelMin: itemLevelMin,
                    itemLevelMax: itemLevelMax,
                    rarity: rarity,
                    selectedMods: selectedMods,
                    isExactSearch: false,
                    quality: qualityVal,
                    armour: armourVal,
                    evasion: evasionVal,
                    energyShield: energyShieldVal,
                    corrupted: corrupted,
                    identified: identified,
                    itemFlag: form.ItemFlag,
                    progress: progress);
            }

            AppLogger.Instance.Info($"搜索结果：total={searchResult.Total}, ids={searchResult.ResultIds.Count}");

            if (searchResult.ResultIds.Count == 0)
            {
                vm.ShowError("未找到该物品的市集挂单");
                return;
            }

            // 存储搜索上下文到 ViewModel，供翻页使用。
            vm.SearchId = searchResult.SearchId;
            vm.AllResultIds = searchResult.ResultIds;
            vm.FetchPageCallback = FetchOverlayPageAsync;

            var pageIds = searchResult.ResultIds.Take(PriceOverlayViewModel.PageSize).ToList();
            var listings = await _tradeService.FetchAsync(
                searchResult.SearchId,
                pageIds,
                EffectivePoeSessionId);

            if (listings.Count == 0)
            {
                vm.ShowError("未找到有效价格");
                return;
            }

            var totalPages = (int)Math.Ceiling(searchResult.ResultIds.Count / (double)PriceOverlayViewModel.PageSize);
            vm.ShowResults(
                $"共 {searchResult.Total} 条结果，第 1/{totalPages} 页，本页 {listings.Count} 条",
                listings);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "叠加层搜索失败");
            vm.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 翻页回调：fetch 指定页的结果列表。
    /// </summary>
    private async Task FetchOverlayPageAsync(PriceOverlayViewModel vm, int newPage)
    {
        if (string.IsNullOrEmpty(vm.SearchId) || vm.AllResultIds.Count == 0) return;

        var pageIds = vm.AllResultIds
            .Skip(newPage * PriceOverlayViewModel.PageSize)
            .Take(PriceOverlayViewModel.PageSize)
            .ToList();

        if (pageIds.Count == 0) return;

        AppLogger.Instance.Info($"翻页请求：page={newPage + 1}, fetchCount={pageIds.Count}");

        var listings = await _tradeService.FetchAsync(
            vm.SearchId,
            pageIds,
            EffectivePoeSessionId);

        if (listings.Count == 0)
        {
            vm.ErrorMessage = "该页无有效数据";
            return;
        }

        var totalPages = (int)Math.Ceiling(vm.AllResultIds.Count / (double)PriceOverlayViewModel.PageSize);
        vm.ResultSummary = $"共 {vm.AllResultIds.Count} 条结果，第 {newPage + 1}/{totalPages} 页，本页 {listings.Count} 条";
        vm.UpdatePageResults(listings, newPage);
    }

    /// <summary>
    /// 确保 XiletradePriceService 使用设置中保存的语言（首次使用或语言变更后同步）。
    /// </summary>
    private void EnsurePriceCheckerLanguage()
    {
        var svc = XiletradePriceService.Instance;
        if (svc.CurrentLanguage != PriceCheckerLanguage)
        {
            svc.SetLanguage(PriceCheckerLanguage);
        }
    }

    /// <summary>
    /// 显示叠加层窗口（单实例：关闭旧的再显示新的）。
    /// </summary>
    private void ShowOverlay(PriceOverlayViewModel viewModel)
    {
        // 关闭已有叠加层。
        if (_currentOverlay != null)
        {
            try
            {
                _currentOverlay.Close();
            }
            catch { /* 忽略关闭异常 */ }
            _currentOverlay = null;
        }

        _currentOverlay = new PriceOverlayWindow(viewModel);
        _currentOverlay.Closed += (_, _) => _currentOverlay = null;
        // 参考 xiletrade-master NavigationService.ShowMainView: ShowActivated = false
        // overlay 显示时不抢焦点，游戏保持前台，后续按热键的 Ctrl+C 仍发到游戏。
        _currentOverlay.ShowActivated = false;
        _currentOverlay.Show();
    }

    /// <summary>
    /// 用 Topmost 叠加层显示错误信息，确保在游戏上方可见。
    /// </summary>
    private void ShowOverlayError(string title, string detail)
    {
        var viewModel = new PriceOverlayViewModel
        {
            Title = title,
            IsConfigMode = false,
            ErrorMessage = detail,
        };
        ShowOverlay(viewModel);
    }

    private void RefreshDetectedGameMode()
    {
        var info = GameModeDetector.Detect(GameDirectory);
        DetectedGameMode = info.IsValid ? info.DisplayName : (string.IsNullOrWhiteSpace(info.ErrorMessage) ? "未检测" : info.ErrorMessage);

        // 区服变化时重建价格/交易服务。
        var newIsChina = !info.IsValid || info.IsChina;
        var serverChanged = newIsChina != _isChinaServer;
        if (serverChanged)
        {
            _isChinaServer = newIsChina;
            RebuildPriceAndTradeServices();
            PriceDataSourceLabel = _priceService.DataSourceLabel;
            // 区服切换时重置查价器默认语言：国服=简体中文(9)，国际服=繁体中文(8)
            PriceCheckerLanguage = _isChinaServer ? 9 : 8;
            OnPropertyChanged(nameof(PricePageTitle));
            OnPropertyChanged(nameof(IsChinaServer));

            // 区服切换后赛季列表也需要重新拉取（国服中文赛季名 vs 国际服英文赛季名），
            // 否则切换到国际服后 AvailableLeagues 仍是国服的中文列表，下拉框显示错乱。
            // 不阻塞 UI，与构造函数一致使用 Task.Run 异步刷新。
            _ = Task.Run(async () => await ValidateLeagueAsync());
        }

        // 始终校验赛季名与区服是否匹配，避免启动时 saved league 与当前区服不一致。
        // 国服赛季名是中文（如"奥杜尔秘符"），国际服是英文（如"Runes of Aldur"）。
        // 默认留空，由 ValidateLeagueAsync 从 API 获取后选 leagues[0]（当前赛季，人数最多）。
        if (string.IsNullOrWhiteSpace(PriceCheckerLeague) ||
            PriceCheckerLeague == "永久" ||
            PriceCheckerLeague == "永久（专家）" ||
            PriceCheckerLeague == "Standard" ||
            PriceCheckerLeague == "Hardcore")
        {
            PriceCheckerLeague = "";
        }
        else if (serverChanged)
        {
            // 区服切换且用户有自定义赛季名时，记录日志。
            AppLogger.Instance.Info($"区服切换，保留用户自定义赛季：{PriceCheckerLeague}");
        }

        if (serverChanged)
        {
            AppLogger.Instance.Info($"区服切换：IsChina={_isChinaServer}, DataSource={PriceDataSourceLabel}, League={PriceCheckerLeague}");
        }
        else
        {
            AppLogger.Instance.Info($"区服检测：IsChina={_isChinaServer}, League={PriceCheckerLeague}");
        }
    }

    /// <summary>
    /// 从交易 API 获取赛季列表，校正当前赛季名。
    /// 如果当前赛季不在列表中，自动切换到第一个赛季（当前临时赛季）。
    /// 参考 xiletrade-master 的 DataUpdaterService.LeaguesUpdate。
    /// </summary>
    private async Task ValidateLeagueAsync()
    {
        try
        {
            var leagues = await _tradeService.GetLeaguesAsync();
            if (leagues.Count == 0)
            {
                AppLogger.Instance.Warn("获取赛季列表为空，保持当前赛季设置");
                return;
            }

            // 在 UI 线程更新可用赛季列表，供下拉框绑定。
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                AvailableLeagues.Clear();
                foreach (var lg in leagues)
                {
                    AvailableLeagues.Add(lg);
                }
            });

            // 如果当前赛季不在列表中，或为永久服（玩家人数少），自动切换到第一个赛季（当前赛季）。
            var permanentLeagues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "永久", "永久（专家）", "Standard", "Hardcore"
            };
            if (string.IsNullOrWhiteSpace(PriceCheckerLeague) ||
                !leagues.Contains(PriceCheckerLeague) ||
                permanentLeagues.Contains(PriceCheckerLeague))
            {
                var oldLeague = PriceCheckerLeague;
                PriceCheckerLeague = leagues[0];
                AppLogger.Instance.Info($"赛季默认选择第0个：'{oldLeague}' → '{leagues[0]}'（可用赛季：{string.Join(", ", leagues)}）");
            }
            else
            {
                AppLogger.Instance.Info($"赛季名验证通过：{PriceCheckerLeague}（可用赛季：{string.Join(", ", leagues)}）");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"获取赛季列表异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 根据当前区服重建价格服务与交易搜索服务。
    /// 国服：PoecurrencyPriceService + poe.game.qq.com 交易接口
    /// 国际服：Poe2ScoutPriceService + www.pathofexile.com 交易接口
    /// </summary>
    private void RebuildPriceAndTradeServices()
    {
        if (_isChinaServer)
        {
            _priceService = new PoecurrencyPriceService(_httpClient)
            {
                Token = CurrencyPriceToken,
            };
            _tradeService = new PoeTradeService(_httpClient, isChina: true);
        }
        else
        {
            _priceService = new Poe2ScoutPriceService(_httpClient, league: "runes");
            _tradeService = new PoeTradeService(_httpClient, isChina: false);
        }

        // 国服和国际服都需要翻译器：
        // - 国际服：把 poe2scout 返回的英文名替换为繁体中文。
        // - 国服：合并国际服补充数据时，把英文名翻译为中文。
        // 翻译表来源：从已提取的 datc64 双文件构建（英文+目标语言），缓存到本地。
        _itemNameTranslator = new ItemNameTranslator();
        var langCode = _isChinaServer ? "zh-CN" : Poe2LanguageDetector.GetDefaultLanguageCode(isChina: false);
        _translationLoadTask = Task.Run(async () =>
        {
            if (!await _itemNameTranslator.LoadCacheAsync(langCode))
            {
                // 缓存不存在时，尝试从已提取的 datc64 文件构建（如安装补丁后）。
                // 已迁移到 %LOCALAPPDATA%\Poe2PriceGui\output\。
                var outputDir = AppDataPath.Output;
                if (_itemNameTranslator.TryBuildFromExtractedFiles(outputDir, langCode))
                {
                    await _itemNameTranslator.SaveCacheAsync(langCode);
                }
            }
        });

        PriceDataSourceLabel = _priceService.DataSourceLabel;
        OnPropertyChanged(nameof(PricePageTitle));
        OnPropertyChanged(nameof(IsChinaServer));

        // 区服切换后 EffectivePoeSessionId 指向的字段已变化，统一刷新登录状态、命令可用性及 stats 预加载。
        OnPoeSessionIdChanged();
    }

    /// <summary>
    /// 调试用：强制切换国服/国际服模式，便于在未设置国际服游戏目录时测试国际服价格显示。
    /// </summary>
    private void ForceSwitchServer()
    {
        _isChinaServer = !_isChinaServer;
        RebuildPriceAndTradeServices();
        // 区服切换时重置查价器默认语言：国服=简体中文(9)，国际服=繁体中文(8)
        PriceCheckerLanguage = _isChinaServer ? 9 : 8;

        // 切换默认赛季（仅在为默认值时自动切换，避免覆盖用户自定义）。
        var defaultCn = "奥杜尔秘符";
        var defaultIntl = "Runes of Aldur";
        if (_isChinaServer && (string.IsNullOrWhiteSpace(PriceCheckerLeague) || PriceCheckerLeague == defaultIntl))
        {
            PriceCheckerLeague = defaultCn;
        }
        else if (!_isChinaServer && (string.IsNullOrWhiteSpace(PriceCheckerLeague) || PriceCheckerLeague == defaultCn))
        {
            PriceCheckerLeague = defaultIntl;
        }

        var modeText = _isChinaServer ? "国服" : "国际服";
        _toastService.ShowInfo($"已强制切换为{modeText}模式（调试）");
        AppLogger.Instance.Info($"强制切换区服：IsChina={_isChinaServer}, DataSource={PriceDataSourceLabel}, League={PriceCheckerLeague}");

        // 区服切换后重新拉取赛季列表（与 RefreshDetectedGameMode 一致）。
        _ = Task.Run(async () => await ValidateLeagueAsync());
    }

    private void RefreshLastRefreshTimeDisplay()
    {
        LastRefreshTime = _settings.LastRefreshTime.HasValue
            ? _settings.LastRefreshTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "无";
    }

    private void ShowAutoDetectGameDirectory()
    {
        var viewModel = new AutoDetectGameDirectoryViewModel();
        var window = new AutoDetectGameDirectoryWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel,
        };

        viewModel.OnApply = path =>
        {
            GameDirectory = path;
            window.DialogResult = true;
            window.Close();
        };
        viewModel.OnCancel = () =>
        {
            window.DialogResult = false;
            window.Close();
        };

        window.ShowDialog();
    }

    private void OpenPriceCheckerLoginBrowser()
    {
        try
        {
            // 注入已保存的 POESESSID 以恢复登录态：按当前区服取对应字段。
            var existing = _isChinaServer ? PriceCheckerPoeSessionId : PriceCheckerIntlPoeSessionId;
            var window = new LoginBrowserWindow(existing, isChina: _isChinaServer)
            {
                Owner = Application.Current.MainWindow,
            };

            if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.CapturedPoeSessionId))
            {
                // 按当前区服写入对应字段，避免国服/国际服 Cookie 互相覆盖。
                if (_isChinaServer)
                {
                    PriceCheckerPoeSessionId = window.CapturedPoeSessionId;
                }
                else
                {
                    PriceCheckerIntlPoeSessionId = window.CapturedPoeSessionId;
                }
                _toastService.ShowSuccess("已自动获取并保存 POESESSID");
                AppLogger.Instance.Info($"通过内置浏览器获取 POESESSID 成功（{(_isChinaServer ? "国服" : "国际服")}）");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "打开内置登录浏览器失败");
            _toastService.ShowError($"打开登录窗口失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 弹出热键捕获窗口：监听下一次按键组合，更新 PriceCheckerHotkey。
    /// 仅接受带修饰键（Ctrl/Alt/Shift/Win）的组合或功能键（F1-F12）。
    /// </summary>
    private void CaptureHotkey()
    {
        var owner = Application.Current.MainWindow;
        var captureWindow = new Window
        {
            Owner = owner,
            Title = "捕获热键",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
        };

        var label = new TextBlock
        {
            Text = "请按下热键组合（Esc 取消）\n例如：Ctrl+D、Alt+F1、Shift+Q",
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        captureWindow.Content = label;

        string? captured = null;

        captureWindow.KeyDown += (_, e) =>
        {
            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                captureWindow.Close();
                return;
            }

            // 修饰键单独按下不处理，等待主键。
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return;
            }

            var parts = new List<string>();
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            // 功能键（F1-F12）可单独使用，其余键必须配合修饰键。
            var isFunctionKey = e.Key.ToString().StartsWith("F", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(e.Key.ToString()[1..], out var fnNum) && fnNum >= 1 && fnNum <= 12;

            if (parts.Count == 0 && !isFunctionKey)
            {
                label.Text = "需要配合修饰键（Ctrl/Alt/Shift/Win）\n请重新按下热键组合（Esc 取消）";
                return;
            }

            parts.Add(e.Key.ToString());
            captured = string.Join("+", parts);
            captureWindow.Close();
        };

        captureWindow.ShowDialog();

        if (!string.IsNullOrWhiteSpace(captured))
        {
            PriceCheckerHotkey = captured;
            _toastService.ShowSuccess($"热键已更新为 {captured}");
        }
    }

    /// <summary>
    /// 设置页操作状态文本。
    /// </summary>
    public string SettingsStatusMessage
    {
        get => _settingsStatusMessage;
        set => SetProperty(ref _settingsStatusMessage, value);
    }

    /// <summary>
    /// 当前应用版本号。
    /// </summary>
    public string AppVersion => _updateService.CurrentVersion;

    #region 泥人补丁

    /// <summary>
    /// 所有可选补丁元数据（17 个），用于 UI checkbox 网格绑定。
    /// </summary>
    public IReadOnlyList<PatchInfo> SmootherAllPatches { get; } = PatchCatalog.AllPatches;

    /// <summary>
    /// UI 可见的预设元数据（过滤掉 IsHidden 的预设），用于 UI 预设按钮绑定。
    /// </summary>
    public IReadOnlyList<PresetInfo> SmootherAllPresets { get; } =
        PatchCatalog.AllPresets.Where(p => !p.IsHidden).ToList();

    /// <summary>
    /// 用户勾选的补丁集合（绑定到 checkbox）。变更时持久化到 settings。
    /// 使用 PatchSelectionItem wrapper 提供 INotifyPropertyChanged，支持双向绑定。
    /// </summary>
    public ObservableCollection<PatchSelectionItem> SmootherPatchItems { get; } = new();

    private double _smootherCameraZoom = 2.4;
    /// <summary>
    /// 相机 zoom 倍率（1.2 ~ 2.4），用户可拖动滑块调节。变更时持久化到 settings。
    /// </summary>
    public double SmootherCameraZoom
    {
        get => _smootherCameraZoom;
        set
        {
            if (SetProperty(ref _smootherCameraZoom, value))
            {
                _settings.SmootherCameraZoom = value;
                _settingsService.Save(_settings);
            }
        }
    }

    /// <summary>
    /// 初始化泥人补丁勾选状态：从 settings 读取已保存的勾选补丁名列表，构建 PatchSelectionItem。
    /// 在构造函数末尾调用一次。
    /// </summary>
    private void InitSmootherPatchChecked()
    {
        SmootherPatchItems.Clear();
        var saved = _settings.SmootherSelectedPatches ?? new();
        foreach (var patch in SmootherAllPatches)
        {
            var isChecked = saved.Contains(patch.Name, StringComparer.OrdinalIgnoreCase);
            // var groupName = patch.Id switch
            // {
            //     PatchId.Effects or PatchId.Effects_New or PatchId.Test or PatchId.EffectNone => "effects",
            //     _ => null,
            // };
            var item = new PatchSelectionItem(patch, isChecked, patch.GroupName);
            item.PropertyChanged += OnSmootherPatchItemPropertyChanged;
            SmootherPatchItems.Add(item);
        }

        // 确保单选组内至多只有一个被勾选（settings 里可能同时存在多个）。
        EnsureSingleRadioSelection();
    }

    /// <summary>
    /// 确保同一单选组内只有一个补丁被选中。如果多个被选中，保留第一个。
    /// </summary>
    private void EnsureSingleRadioSelection()
    {
        var groups = SmootherPatchItems
            .Where(item => item.IsRadio && item.IsChecked)
            .GroupBy(item => item.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            foreach (var item in group.Skip(1))
            {
                item.IsChecked = false;
            }
        }
    }

    /// <summary>
    /// 持久化当前勾选状态到 settings。
    /// </summary>
    private void SaveSmootherPatchChecked()
    {
        var list = SmootherPatchItems
            .Where(item => item.IsChecked)
            .Select(item => item.Info.Name)
            .ToList();
        _settings.SmootherSelectedPatches = list;
        _settingsService.Save(_settings);
    }

    /// <summary>
    /// 处理单选组互斥：同一 GroupName 中只能有一个被选中。
    /// 任何勾选状态变化都会持久化到 settings。
    /// </summary>
    private void OnSmootherPatchItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PatchSelectionItem.IsChecked) || sender is not PatchSelectionItem changed)
        {
            return;
        }

        // 单选组互斥：同一 GroupName 中已勾选的其他项取消勾选。
        if (changed.IsChecked && changed.IsRadio)
        {
            foreach (var item in SmootherPatchItems)
            {
                if (item != changed && item.IsRadio &&
                    item.GroupName.Equals(changed.GroupName, StringComparison.OrdinalIgnoreCase) &&
                    item.IsChecked)
                {
                    item.IsChecked = false;
                }
            }
        }

        SaveSmootherPatchChecked();
    }

    /// <summary>
    /// 根据当前勾选状态返回选中的补丁列表。Camera 强制移到末尾（参考 tiny-poe2smoother-gui.rs:568-572）。
    /// </summary>
    private PatchId[] GetSelectedPatches()
    {
        var selected = SmootherPatchItems
            .Where(item => item.IsChecked)
            .Select(item => item.Info.Id)
            .ToList();
        // Camera 强制最后执行：从列表中移除并追加到末尾。
        var cameraIdx = selected.IndexOf(PatchId.Camera);
        if (cameraIdx >= 0)
        {
            selected.RemoveAt(cameraIdx);
            selected.Add(PatchId.Camera);
        }
        return selected.ToArray();
    }

    /// <summary>"全选"按钮：勾选所有补丁。单选组只保留第一个。</summary>
    private void SmootherSelectAll()
    {
        foreach (var item in SmootherPatchItems)
            item.IsChecked = true;
        EnsureSingleRadioSelection();
        SaveSmootherPatchChecked();
    }

    /// <summary>"全不选"按钮：取消所有勾选。</summary>
    private void SmootherSelectNone()
    {
        foreach (var item in SmootherPatchItems)
            item.IsChecked = false;
        SaveSmootherPatchChecked();
    }

    /// <summary>
    /// "预设按钮"：先清空所有勾选，再勾选指定预设包含的补丁（参考 tiny-poe2smoother-gui.rs:374-380）。
    /// parameter = 预设名（如 "performance"、"maps-revealed"）。
    /// </summary>
    private void SmootherApplyPreset(string? presetName)
    {
        if (string.IsNullOrEmpty(presetName)) return;
        var preset = PatchCatalog.AllPresets.FirstOrDefault(p =>
            p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
        if (preset == null) return;

        // 先全不选，再勾选预设包含的补丁（替换模式，避免之前勾选的残留）
        foreach (var item in SmootherPatchItems)
        {
            item.IsChecked = preset.Patches.Contains(item.Info.Id);
        }
        EnsureSingleRadioSelection();
        SaveSmootherPatchChecked();
        _toastService.ShowInfo($"已应用预设：{preset.DisplayName}");
    }

    /// <summary>
    /// 检测 POE2 游戏进程是否正在运行（参考 tiny-poe2smoother app.rs ensure_game_not_running）。
    /// </summary>
    private static bool IsGameRunning()
    {
        return Process.GetProcessesByName("PathOfExile2Steam").Length > 0
            || Process.GetProcessesByName("PathOfExile2").Length > 0
            || Process.GetProcessesByName("PathOfExile2_x64").Length > 0;
    }

    private string _smootherProgressText = "";
    /// <summary>
    /// 泥人补丁进度描述文本，显示在进度条上方。
    /// </summary>
    public string SmootherProgressText
    {
        get => _smootherProgressText;
        set => SetProperty(ref _smootherProgressText, value);
    }

    private int _smootherProgressValue;
    /// <summary>
    /// 泥人补丁进度百分比（0-100）。
    /// </summary>
    public int SmootherProgressValue
    {
        get => _smootherProgressValue;
        set => SetProperty(ref _smootherProgressValue, value);
    }

    private bool _isSmootherBusy;
    /// <summary>
    /// 泥人补丁是否正在执行（控制进度条可见性）。
    /// </summary>
    public bool IsSmootherBusy
    {
        get => _isSmootherBusy;
        set => SetProperty(ref _isSmootherBusy, value);
    }

    private async Task SmootherApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
        {
            _toastService.ShowError("请先在设置页配置有效的游戏目录");
            return;
        }

        // 安全校验1：检测游戏进程是否运行（参考 tiny-poe2smoother app.rs ensure_game_not_running）。
        if (IsGameRunning())
        {
            _toastService.ShowError("检测到 POE2 游戏正在运行，请先关闭游戏再应用补丁");
            return;
        }

        var patches = GetSelectedPatches();
        if (patches.Length == 0)
        {
            _toastService.ShowError("请先勾选至少一个补丁");
            return;
        }

        var patchNames = string.Join(", ", patches.Select(p => PatchCatalog.PatchDisplayName(p)));
        _toastService.ShowInfo($"正在应用 {patches.Length} 个补丁：{patchNames}，请确保游戏已关闭");

        IsBusy = true;
        IsSmootherBusy = true;
        SmootherProgressValue = 0;
        SmootherProgressText = "准备中...";
        SettingsStatusMessage = $"正在应用泥人补丁（{patches.Length} 个）...";

        // 必须把 SmootherPatchService 构造 + IsPatchApplied + Apply 全部放进 Task.Run，
        // 否则 GGPK 模式下打开 100GB+ Content.ggpk 会卡 UI 线程 30+ 秒。
        try
        {
            var progress = new Progress<SmootherProgress>(p =>
            {
                SmootherProgressValue = p.Percent;
                SmootherProgressText = p.Description;
            });
            var report = await Task.Run(() =>
            {
                var service = new SmootherPatchService(GameDirectory);

                // 安全校验2：检测补丁是否已应用，拒绝重复打补丁。
                // 这一步在 GGPK 模式下要打开 Content.ggPK，必须在后台线程做。
                if (service.IsPatchApplied())
                {
                    return SmootherPatchReport.CreateFailure("检测到泥人补丁已应用，请先还原再重新应用");
                }

                return service.Apply(patches, zoom: SmootherCameraZoom, progress: progress);
            });
            if (report.Success)
            {
                var msg2 = $"已应用 {patches.Length} 个补丁：修改 {report.ChangedFileCount} 个文件";
                foreach (var (patch, count) in report.PatchHitCounts)
                {
                    msg2 += $"\n  {patch}: {count}";
                }
                SettingsStatusMessage = msg2.Replace("\n", " | ");
                SmootherProgressValue = 100;
                SmootherProgressText = "完成";
                _toastService.ShowSuccess($"泥人补丁应用成功：修改 {report.ChangedFileCount} 个文件");
                AppLogger.Instance.Info($"泥人补丁应用成功：{msg2}");
            }
            else
            {
                SettingsStatusMessage = $"泥人补丁应用失败：{report.ErrorMessage}";
                SmootherProgressText = $"失败：{report.ErrorMessage}";
                _toastService.ShowError($"泥人补丁应用失败：{report.ErrorMessage}");
                AppLogger.Instance.Error($"泥人补丁应用失败：{report.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "泥人补丁应用异常");
            SettingsStatusMessage = $"泥人补丁应用异常：{ex.Message}";
            SmootherProgressText = $"异常：{ex.Message}";
            _toastService.ShowError($"泥人补丁应用异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsSmootherBusy = false;
        }
    }

    private async Task SmootherPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
        {
            _toastService.ShowError("请先在设置页配置有效的游戏目录");
            return;
        }

        IsBusy = true;
        IsSmootherBusy = true;
        SmootherProgressValue = 0;
        SmootherProgressText = "准备中...";
        var patches = GetSelectedPatches();
        if (patches.Length == 0)
        {
            _toastService.ShowError("请先勾选至少一个补丁");
            IsBusy = false;
            IsSmootherBusy = false;
            return;
        }
        SettingsStatusMessage = $"正在预览 {patches.Length} 个补丁...";

        try
        {
            var service = new SmootherPatchService(GameDirectory);
            var progress = new Progress<SmootherProgress>(p =>
            {
                SmootherProgressValue = p.Percent;
                SmootherProgressText = p.Description;
            });
            var report = await Task.Run(() => service.Preview(patches, zoom: SmootherCameraZoom, progress: progress));
            if (report.Success)
            {
                var msg = $"预览：将修改 {report.ChangedFileCount} 个文件";
                foreach (var (patch, count) in report.PatchHitCounts)
                {
                    msg += $"\n  {patch}: {count}";
                }
                SettingsStatusMessage = msg.Replace("\n", " | ");
                SmootherProgressValue = 100;
                SmootherProgressText = "预览完成";
                _toastService.ShowInfo($"泥人补丁预览：将修改 {report.ChangedFileCount} 个文件");
                AppLogger.Instance.Info($"泥人补丁预览：{msg}");
            }
            else
            {
                SettingsStatusMessage = $"泥人补丁预览失败：{report.ErrorMessage}";
                SmootherProgressText = $"失败：{report.ErrorMessage}";
                _toastService.ShowError($"泥人补丁预览失败：{report.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "泥人补丁预览异常");
            SettingsStatusMessage = $"泥人补丁预览异常：{ex.Message}";
            SmootherProgressText = $"异常：{ex.Message}";
            _toastService.ShowError($"泥人补丁预览异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsSmootherBusy = false;
        }
    }

    private async Task SmootherRestoreAsync()
    {
        // 安全校验：还原也需要游戏关闭。
        if (IsGameRunning())
        {
            _toastService.ShowError("检测到 POE2 游戏正在运行，请先关闭游戏再还原补丁");
            return;
        }

        IsBusy = true;
        IsSmootherBusy = true;
        SmootherProgressValue = 0;
        SmootherProgressText = "正在还原...";
        SettingsStatusMessage = "正在还原泥人补丁...";

        try
        {
            var service = new SmootherPatchService(GameDirectory);
            var count = await Task.Run(() => service.Restore());
            SettingsStatusMessage = $"泥人补丁已还原（恢复 {count} 个文件）";
            SmootherProgressValue = 100;
            SmootherProgressText = "还原完成";
            _toastService.ShowSuccess($"泥人补丁已还原（恢复 {count} 个文件）");
            AppLogger.Instance.Info($"泥人补丁还原成功：恢复 {count} 个文件");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "泥人补丁还原异常");
            SettingsStatusMessage = $"泥人补丁还原异常：{ex.Message}";
            SmootherProgressText = $"异常：{ex.Message}";
            _toastService.ShowError($"泥人补丁还原异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsSmootherBusy = false;
        }
    }

    private async Task SmootherCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
        {
            _toastService.ShowError("请先在设置页配置有效的游戏目录");
            return;
        }

        IsBusy = true;
        SettingsStatusMessage = "正在检测泥人补丁状态...";

        try
        {
            var service = new SmootherPatchService(GameDirectory);
            var status = await Task.Run(() => service.GetDetailedStatus());
            var hasBackup = service.HasBackup;

            var summary = status.ToSummary();
            var backupInfo = $"备份：{(hasBackup ? "存在" : "不存在")}";

            // 完整状态消息（状态栏 + 日志）
            var fullMsg = $"{summary}\n{backupInfo}";
            SettingsStatusMessage = summary.Replace("\n", " | ") + " | " + backupInfo;

            // 任务3：检测状态改为右上角 Toast 提示，无需点击确认。
            // 检测状态信息较多，按行拆成多条 Toast 显示。
            var toastTitle = status.OurApplied
                ? $"泥人补丁已应用（{status.OurFileCount} 文件，{status.OurBundleCount} bundle）"
                : "泥人补丁：未应用";
            _toastService.ShowInfo(toastTitle);
            foreach (var line in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _toastService.ShowInfo(line);
            }
            _toastService.ShowInfo(backupInfo);

            AppLogger.Instance.Info($"泥人补丁检测：\n{fullMsg}");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "泥人补丁检测异常");
            SettingsStatusMessage = $"泥人补丁检测异常：{ex.Message}";
            _toastService.ShowError($"泥人补丁检测异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    #region 技能特效补丁

    /// <summary>
    /// 从程序目录 data/patcheffect、data/orginpatcheffect 以及 AppDataPath.Data 下对应目录
    /// 加载 zip 补丁文件列表。优先程序目录，再 AppData，按文件名排序；同名文件程序目录优先。
    /// </summary>
    private void LoadSkillPatchFiles()
    {
        LoadSkillPatchFilesCore(_skillPatchFiles, "patcheffect");
        LoadSkillPatchFilesCore(_skillRestoreFiles, "orginpatcheffect");

        SelectedSkillPatch = _skillPatchFiles.FirstOrDefault() ?? "";
        SelectedSkillRestore = _skillRestoreFiles.FirstOrDefault() ?? "";

        (ApplySkillPatchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplySkillRestoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static void LoadSkillPatchFilesCore(ObservableCollection<string> collection, string subDirName)
    {
        collection.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appDataDir = subDirName switch
        {
            "patcheffect" => AppDataPath.SkillPatchEffect,
            "orginpatcheffect" => AppDataPath.SkillRestoreEffect,
            _ => Path.Combine(AppDataPath.Data, subDirName)
        };
        var dirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data", subDirName),
            appDataDir
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*.zip").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (seen.Add(name))
                    collection.Add(file);
            }
        }
    }

    /// <summary>
    /// 应用选中的技能特效补丁或还原文件到游戏目录。
    /// </summary>
    private async Task ApplySkillPatchAsync(bool isRestore)
    {
        var zipPath = isRestore ? SelectedSkillRestore : SelectedSkillPatch;
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            _toastService.ShowError("请选择有效的补丁文件");
            return;
        }

        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
        {
            _toastService.ShowError("请先在设置页配置有效的游戏目录");
            return;
        }

        var actionName = isRestore ? "还原" : "应用";
        var fileName = Path.GetFileName(zipPath);

        // 用户确认。
        var confirm = MessageBox.Show(
            $"确定要{actionName}技能特效补丁吗？\n\n文件：{fileName}\n游戏目录：{GameDirectory}\n\n请确保游戏已关闭。",
            $"确认{actionName}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        SkillPatchStatus = $"正在{actionName} {fileName}...";

        try
        {
            var result = await _patchInstaller.ApplyEffectPatchZipAsync(GameDirectory, zipPath);
            if (result.Success)
            {
                _toastService.ShowSuccess($"{actionName}完成：{fileName}");
                AppLogger.Instance.Info($"技能特效补丁{actionName}成功：{fileName}，模式：{result.GameMode}，路径：{result.InstalledPath}");
            }
            else
            {
                _toastService.ShowError($"{actionName}失败：{result.ErrorMessage}");
                AppLogger.Instance.Warn($"技能特效补丁{actionName}失败：{fileName}，原因：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, $"技能特效补丁{actionName}异常");
            _toastService.ShowError($"{actionName}异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            SkillPatchStatus = "";
        }
    }

    private void OpenSkillPatchDirectory()
    {
        Directory.CreateDirectory(AppDataPath.SkillPatchEffect);
        OpenDirectoryInExplorer(AppDataPath.SkillPatchEffect);
    }

    private void OpenSkillRestoreDirectory()
    {
        Directory.CreateDirectory(AppDataPath.SkillRestoreEffect);
        OpenDirectoryInExplorer(AppDataPath.SkillRestoreEffect);
    }

    #endregion

    #region 设置-目录

    private void OpenAppDataDirectory()
    {
        Directory.CreateDirectory(AppDataPath.Root);
        OpenDirectoryInExplorer(AppDataPath.Root);
    }

    private void OpenProgramDataDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dir);
        OpenDirectoryInExplorer(dir);
    }

    private static void OpenDirectoryInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, $"打开目录失败：{path}");
            MessageBox.Show($"打开目录失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    /// <summary>
    /// 检查 GitHub Releases 是否有新版本。
    /// </summary>
    public async Task CheckForUpdateAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在检查更新...";

        try
        {
            var updateInfo = await _updateService.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                SettingsStatusMessage = $"当前已是最新版本（v{AppVersion}）";
                _toastService.ShowInfo($"当前已是最新版本（v{AppVersion}）");
                return;
            }

            var newVersion = updateInfo.TargetFullRelease.Version;
            AppLogger.Instance.Info($"发现新版本：{newVersion}");

            // 询问用户是否下载并安装更新。
            var result = MessageBox.Show(
                $"发现新版本 v{newVersion}！\n当前版本：v{AppVersion}\n\n是否立即下载并安装更新？",
                "发现新版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                SettingsStatusMessage = "已跳过本次更新";
                return;
            }

            // 下载更新包。
            SettingsStatusMessage = $"正在下载更新 v{newVersion}...";
            var downloaded = await _updateService.DownloadUpdatesAsync(updateInfo, progressPercent =>
            {
                SettingsStatusMessage = $"正在下载更新... {progressPercent}%";
            });

            if (!downloaded)
            {
                SettingsStatusMessage = "更新下载失败，请稍后重试";
                _toastService.ShowError("更新下载失败，请稍后重试");
                return;
            }

            SettingsStatusMessage = "更新下载完成，即将重启并安装...";
            _toastService.ShowSuccess("更新下载完成，即将重启并安装");

            // 短暂延迟让用户看到提示，然后应用更新并重启。
            await Task.Delay(1500);
            _updateService.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "检查更新失败");
            SettingsStatusMessage = $"检查更新失败：{ex.Message}";
            _toastService.ShowError($"检查更新失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 从当前价格源（国服 poecurrency.top / 国际服 poe2scout.com）抓取最新价格。
    /// </summary>
    public async Task RefreshPricesAsync()
    {
        IsBusy = true;
        StatusMessage = $"正在从 {_priceService.DataSourceLabel} 获取价格...";
        AppLogger.Instance.Info($"开始刷新价格，数据源：{_priceService.DataSourceLabel}");

        try
        {
            // 刷新前先读取本地旧数据，用于后续对比价格变动。
            var oldPrices = _priceDataService.LocalDataExists
                ? await _priceDataService.LoadAsync()
                : [];
            var oldPriceMap = oldPrices.ToDictionary(p => p.ItemName, p => p.PriceExalted);
            AppLogger.Instance.Info($"读取本地旧数据 {oldPrices.Count} 条用于对比");

            var mappingTask = _iconCacheService.LoadMappingAsync();

            // 国服模式：根据"国际服兜底"开关决定是否并行获取国际服数据补充。
            if (_isChinaServer)
            {
                var cnTask = _priceService.FetchPricesAsync();
                Task<ObservableCollection<PoecurrencyItem>>? intlTask = null;
                if (IntlFallbackEnabled)
                {
                    // 用独立实例获取国际服数据，避免影响主服务状态。
                    var intlPriceService = new Poe2ScoutPriceService(_httpClient, league: "runes");
                    intlTask = intlPriceService.FetchPricesAsync();
                }

                if (intlTask != null)
                {
                    await Task.WhenAll(cnTask, intlTask, mappingTask);
                }
                else
                {
                    await Task.WhenAll(cnTask, mappingTask);
                }

                var cnPrices = await cnTask;
                var merged = new List<PoecurrencyItem>(cnPrices);
                var intlSupplementCount = 0;

                if (intlTask != null)
                {
                    var intlPrices = await intlTask;
                    AppLogger.Instance.Info($"国服获取 {cnPrices.Count} 条，国际服获取 {intlPrices.Count} 条");

                    // 确保翻译器已加载完成再使用（从缓存或已提取 datc64 构建）。
                    if (_translationLoadTask != null)
                    {
                        await _translationLoadTask;
                    }

                    // 翻译国际服英文名为中文，翻译表随程序打包，未命中翻译的物品跳过。
                    if (_itemNameTranslator is { HasTranslations: true })
                    {
                        var translated = 0;
                        foreach (var item in intlPrices)
                        {
                            var localized = _itemNameTranslator.Translate(item.ItemName);
                            if (!ReferenceEquals(localized, item.ItemName))
                            {
                                item.ItemName = localized;
                                item.DataSource = "国际服补充";
                                translated++;
                            }
                            // 翻译未命中的物品跳过（保留英文会出现重复且无法和国服去重）。
                        }
                        AppLogger.Instance.Info($"国际服物品名翻译：{translated}/{intlPrices.Count} 条命中");

                        // 合并：以中文名为 key 去重，国服优先。仅加入翻译成功的国际服物品。
                        var cnNames = cnPrices.Select(p => p.ItemName)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in intlPrices.Where(p => p.DataSource == "国际服补充"))
                        {
                            if (!cnNames.Contains(item.ItemName))
                            {
                                merged.Add(item);
                                intlSupplementCount++;
                            }
                        }
                        AppLogger.Instance.Info($"合并完成：国服 {cnPrices.Count} 条 + 国际服补充 {intlSupplementCount} 条 = {merged.Count} 条");
                    }
                    else
                    {
                        AppLogger.Instance.Warn("翻译表未就绪，跳过国际服数据补充（请检查 data/translations/ 目录）");
                    }
                }
                else
                {
                    AppLogger.Instance.Info($"国服获取 {cnPrices.Count} 条（国际服兜底已关闭）");
                }

                var prices = new ObservableCollection<PoecurrencyItem>(merged);
                var changedCount = ComparePriceChanges(prices, oldPriceMap);

                Prices = prices;
                UpdateEditedCount();

                await _priceDataService.SaveAsync(prices);
                AppLogger.Instance.Info($"保存 {prices.Count} 条价格到本地");

                _settings.LastRefreshTime = DateTime.UtcNow;
                _settingsService.Save(_settings);
                RefreshLastRefreshTimeDisplay();

                // 提示用户补充数量。
                if (intlSupplementCount > 0)
                {
                    _toastService.ShowSuccess($"刷新成功，价格变动 {changedCount} 个，国际服补充 {intlSupplementCount} 个");
                    StatusMessage = $"已加载 {prices.Count} 条价格（国际服补充 {intlSupplementCount} 个），正在加载图标...";
                }
                else
                {
                    _toastService.ShowSuccess($"刷新成功，价格变动 {changedCount} 个物品");
                    StatusMessage = $"已加载 {prices.Count} 条价格数据，正在加载图标...";
                }

                _ = Task.Run(async () => await LoadIconsAsync(prices, CancellationToken.None));
                return;
            }

            // 国际服模式：原有逻辑。
            var pricesTask = _priceService.FetchPricesAsync();
            await Task.WhenAll(pricesTask, mappingTask);

            var intlPricesList = await pricesTask;
            var changedCountIntl = 0;
            AppLogger.Instance.Info($"从网络获取价格 {intlPricesList.Count} 条");

            // 确保翻译器已加载完成再使用（从缓存或已提取 datc64 构建）。
            if (_translationLoadTask != null)
            {
                await _translationLoadTask;
            }

            // 国际服：把英文物品名翻译为游戏语言名（如简中）。
            // 翻译在价格对比之前进行，保证旧数据（已翻译）和新数据 key 一致。
            if (_itemNameTranslator is { HasTranslations: true })
            {
                var translated = 0;
                foreach (var item in intlPricesList)
                {
                    var localized = _itemNameTranslator.Translate(item.ItemName);
                    if (!ReferenceEquals(localized, item.ItemName))
                    {
                        item.ItemName = localized;
                        translated++;
                    }
                }
                AppLogger.Instance.Info($"物品名翻译完成：{translated}/{intlPricesList.Count} 条命中");
            }

            // 订阅属性变更以统计编辑次数，并对比价格变动。
            foreach (var item in intlPricesList)
            {
                item.PropertyChanged += OnPriceItemPropertyChanged;

                if (oldPriceMap.TryGetValue(item.ItemName, out var oldPrice) && oldPrice != item.PriceExalted)
                {
                    item.IsPriceChanged = true;
                    changedCountIntl++;
                }
            }

            Prices = intlPricesList;
            UpdateEditedCount();

            await _priceDataService.SaveAsync(intlPricesList);
            AppLogger.Instance.Info($"保存 {intlPricesList.Count} 条价格到本地");

            _settings.LastRefreshTime = DateTime.UtcNow;
            _settingsService.Save(_settings);
            RefreshLastRefreshTimeDisplay();

            _toastService.ShowSuccess($"刷新成功，价格变动 {changedCountIntl} 个物品");
            AppLogger.Instance.Info($"刷新成功，价格变动 {changedCountIntl} 个物品");
            StatusMessage = $"已加载 {intlPricesList.Count} 条价格数据，正在加载图标...";

            _ = Task.Run(async () => await LoadIconsAsync(intlPricesList, CancellationToken.None));
        }
        catch (Exception ex)
        {
            StatusMessage = $"获取失败：{ex.Message}";
            AppLogger.Instance.Error(ex, "刷新价格失败");
            _toastService.ShowError($"刷新失败：{ex.Message}");
            MessageBox.Show($"获取价格失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 订阅属性变更并对比价格变动，返回变动条数。
    /// </summary>
    private int ComparePriceChanges(ObservableCollection<PoecurrencyItem> prices, Dictionary<string, decimal> oldPriceMap)
    {
        var changedCount = 0;
        foreach (var item in prices)
        {
            item.PropertyChanged += OnPriceItemPropertyChanged;

            if (oldPriceMap.TryGetValue(item.ItemName, out var oldPrice) && oldPrice != item.PriceExalted)
            {
                item.IsPriceChanged = true;
                changedCount++;
            }
        }
        return changedCount;
    }

    /// <summary>
    /// 启动时优先加载本地保存的价格数据，并异步加载图标。
    /// </summary>
    private async Task LoadLocalPricesAsync()
    {
        if (!_priceDataService.LocalDataExists)
        {
            StatusMessage = "未找到本地数据，请点击刷新价格从网络获取";
            AppLogger.Instance.Info("启动时未找到本地价格数据");
            return;
        }

        try
        {
            StatusMessage = "正在加载本地价格数据...";
            AppLogger.Instance.Info($"开始从本地加载价格数据：{_priceDataService.DataFilePath}");
            var localPrices = await _priceDataService.LoadAsync();

            // 加载图标映射，用于后续图标显示。
            await _iconCacheService.LoadMappingAsync();

            foreach (var item in localPrices)
            {
                item.PropertyChanged += OnPriceItemPropertyChanged;
            }

            Prices = new ObservableCollection<PoecurrencyItem>(localPrices);
            UpdateEditedCount();
            StatusMessage = $"已从本地加载 {localPrices.Count} 条价格数据，正在加载图标...";
            _toastService.ShowInfo($"已从本地加载 {localPrices.Count} 条价格数据");
            AppLogger.Instance.Info($"已从本地加载 {localPrices.Count} 条价格数据");

            // 异步加载图标并缓存到本地。
            _ = Task.Run(async () => await LoadIconsAsync(localPrices, CancellationToken.None));
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载本地数据失败：{ex.Message}";
            AppLogger.Instance.Error(ex, "加载本地价格数据失败");
            _toastService.ShowError($"加载本地数据失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 编辑后自动保存到本地，带 500ms 去抖。
    /// </summary>
    private async Task AutoSaveAsync()
    {
        _autoSaveDebounceCts?.Cancel();
        _autoSaveDebounceCts = new CancellationTokenSource();
        var token = _autoSaveDebounceCts.Token;

        try
        {
            await Task.Delay(500, token);
            await _priceDataService.SaveAsync(Prices);
            AppLogger.Instance.Info($"自动保存 {Prices.Count} 条价格数据");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"已自动保存 {Prices.Count} 条价格数据";
            });
        }
        catch (OperationCanceledException)
        {
            // 去抖被取消，忽略。
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "自动保存价格数据失败");
        }
    }

    private void OnPriceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PoecurrencyItem.IsEdited))
        {
            UpdateEditedCount();
        }

        if (e.PropertyName == nameof(PoecurrencyItem.PriceExalted) && sender is PoecurrencyItem item)
        {
            item.IsEdited = true;
            _ = AutoSaveAsync();
        }
    }

    private void UpdateEditedCount()
    {
        EditedCount = Prices.Count(p => p.IsEdited);
        ((RelayCommand)ExportPricesCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ExportPatchCommand).RaiseCanExecuteChanged();
        ((RelayCommand)InstallPatchCommand).RaiseCanExecuteChanged();
    }

    private async Task LoadIconsAsync(IEnumerable<PoecurrencyItem> prices, CancellationToken cancellationToken)
    {
        var loadedCount = 0;
        var missingCount = 0;
        var semaphore = new SemaphoreSlim(10, 10);

        async Task LoadIconAsync(PoecurrencyItem item)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // 优先使用 IconUrl 直接加载（国际服 poe2scout 已返回 URL）。
                if (!string.IsNullOrWhiteSpace(item.IconUrl))
                {
                    var icon = await _iconCacheService.GetIconByUrlAsync(item.IconUrl, cancellationToken);
                    if (icon != null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() => item.IconImage = icon);
                        Interlocked.Increment(ref loadedCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref missingCount);
                    }
                    return;
                }

                // 回退：通过物品名查 IconCacheService 映射表（国服 poecurrency.top）。
                if (!_iconCacheService.HasIcon(item.ItemName))
                {
                    AppLogger.Instance.Warn($"图标映射缺失：{item.ItemName}（分类：{item.CategoryLabel}）");
                    Interlocked.Increment(ref missingCount);
                    return;
                }

                var namedIcon = await _iconCacheService.GetIconAsync(item.ItemName, cancellationToken);
                if (namedIcon != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => item.IconImage = namedIcon);
                    Interlocked.Increment(ref loadedCount);
                }
                else
                {
                    AppLogger.Instance.Warn($"图标加载结果为空：{item.ItemName}（分类：{item.CategoryLabel}）");
                    Interlocked.Increment(ref missingCount);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error(ex, $"图标加载异常：{item.ItemName}（分类：{item.CategoryLabel}）");
                Interlocked.Increment(ref missingCount);
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = prices.Select(LoadIconAsync).ToArray();
        await Task.WhenAll(tasks);

        AppLogger.Instance.Info($"图标加载完成：{loadedCount} 个成功，{missingCount} 个缺失");
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusMessage = $"图标加载完成：{loadedCount} 个成功，{missingCount} 个缺失";
        });
    }

    private void CleanCache()
    {
        try
        {
            var (deletedCount, freedBytes) = _iconCacheService.CleanCache();
            var freedMb = freedBytes / 1024.0 / 1024.0;
            CacheStatusMessage = $"已清理 {deletedCount} 个文件，释放 {freedMb:F2} MB";
            AppLogger.Instance.Info($"清理图标缓存：删除 {deletedCount} 个文件，释放 {freedMb:F2} MB");
            ((RelayCommand)OpenLogCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            CacheStatusMessage = $"清理失败：{ex.Message}";
            AppLogger.Instance.Error(ex, "清理图标缓存失败");
        }
    }

    private void OpenLogFile()
    {
        try
        {
            var logPath = AppLogger.Instance.LogFilePath;
            if (!File.Exists(logPath))
            {
                _toastService.ShowWarning("日志文件不存在");
                return;
            }

            Process.Start(new ProcessStartInfo(logPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "打开日志文件失败");
            _toastService.ShowError($"打开日志文件失败：{ex.Message}");
        }
    }

    private void CleanLogs()
    {
        try
        {
            var count = AppLogger.Instance.CleanLogs();
            SettingsStatusMessage = $"已清理 {count} 个日志文件";
            _toastService.ShowSuccess($"已清理 {count} 个日志文件");
            ((RelayCommand)OpenLogCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CleanLogCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "清理日志失败");
            _toastService.ShowError($"清理日志失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 生成翻译表：从游戏 datc64 提取英文+目标语言版，构建英文名→目标语言名映射表。
    /// 翻译表保存到 data/translations/translations_{languageCode}.json，随程序打包发布。
    /// </summary>
    /// <param name="languageCode">目标语言代码（"zh-CN" 或 "zh-TW"）。</param>
    private async Task GenerateTranslationsAsync(string languageCode)
    {
        var langName = languageCode == "zh-TW" ? "繁体中文" : "简体中文";
        IsBusy = true;
        StatusMessage = $"正在提取游戏数据文件并构建{langName}翻译表...";
        AppLogger.Instance.Info($"开始生成{langName}翻译表（{languageCode}）");

        try
        {
            var modeInfo = GameModeDetector.Detect(GameDirectory);
            var extracted = await _patchInstaller.ExtractDatc64ForTranslationAsync(
                GameDirectory, modeInfo, CancellationToken.None);
            if (!extracted)
            {
                _toastService.ShowError("datc64 提取失败，请检查游戏目录和工具配置");
                StatusMessage = $"{langName}翻译表生成失败";
                return;
            }

            var outputDir = AppDataPath.Output;
            var translator = new ItemNameTranslator();
            if (!translator.TryBuildFromExtractedFiles(outputDir, languageCode))
            {
                _toastService.ShowError($"{langName}翻译表构建失败：datc64 解析未产生映射");
                StatusMessage = $"{langName}翻译表生成失败";
                return;
            }

            // 保存到 %LOCALAPPDATA%\Poe2PriceGui\data\translations\（运行时生成目录）。
            // 之前会写入 AppContext.BaseDirectory\data\translations\，导致每次 Release 构建后
            // 该目录被同步进安装包（污染用户程序文件 + 升级时丢失）。
            var runtimeTranslationsDir = AppDataPath.TranslationsRuntime;
            Directory.CreateDirectory(runtimeTranslationsDir);
            var targetPath = Path.Combine(runtimeTranslationsDir, $"translations_{languageCode}.json");
            await using var stream = File.Create(targetPath);
            await System.Text.Json.JsonSerializer.SerializeAsync(stream,
                translator.GetTranslationsSnapshot(),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            AppLogger.Instance.Info($"{langName}翻译表生成成功：{translator.Count} 条映射 → {targetPath}");
            _toastService.ShowSuccess($"{langName}翻译表生成成功：{translator.Count} 条映射");
            StatusMessage = $"{langName}翻译表生成成功：{translator.Count} 条映射";
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, $"生成{langName}翻译表失败");
            _toastService.ShowError($"生成{langName}翻译表失败：{ex.Message}");
            StatusMessage = $"{langName}翻译表生成失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// 手动导出查价器 stats 缓存到 data/stats_cache_debug.json，便于排查词缀匹配问题。
    /// 需要 POESESSID，若缓存未加载会先从 API 拉取。
    /// </summary>
    private async Task ExportStatsCacheAsync()
    {
        if (string.IsNullOrWhiteSpace(EffectivePoeSessionId))
        {
            _toastService.ShowWarning("请先配置 POESESSID");
            return;
        }

        IsBusy = true;
        SettingsStatusMessage = "正在从 API 拉取 stats 数据并导出...";
        try
        {
            var count = await _tradeService.DumpStatsCacheAsync(EffectivePoeSessionId);
            if (count > 0)
            {
                SettingsStatusMessage = $"stats 缓存已导出（{count} 条）到 data/stats_cache_debug.json";
                _toastService.ShowSuccess($"stats 缓存已导出（{count} 条）");
            }
            else
            {
                SettingsStatusMessage = "未获取到 stats 数据，请检查 POESESSID 是否有效及网络连接";
                _toastService.ShowError("未获取到 stats 数据，请检查 POESESSID 和网络");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "导出 stats 缓存失败");
            SettingsStatusMessage = $"导出失败：{ex.Message}";
            _toastService.ShowError($"导出 stats 缓存失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ((RelayCommand)ExportStatsCacheCommand).RaiseCanExecuteChanged();
        }
    }

    private async Task RestoreBackupAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在查找并还原备份...";

        try
        {
            var result = await _patchInstaller.RestoreLatestBackupAsync(GameDirectory);
            if (result.Success)
            {
                SettingsStatusMessage = $"已还原备份：{result.InstalledPath}";
                _toastService.ShowSuccess($"已还原备份：{result.InstalledPath}");
            }
            else
            {
                SettingsStatusMessage = $"还原备份失败：{result.ErrorMessage}";
                _toastService.ShowError($"还原备份失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "还原备份失败");
            SettingsStatusMessage = $"还原备份失败：{ex.Message}";
            _toastService.ShowError($"还原备份失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearBackupsAsync()
    {
        var confirm = MessageBox.Show(
            "你确定清空备份吗？",
            "确认清空备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        SettingsStatusMessage = "正在清空备份...";

        try
        {
            var backupDir = Path.Combine(_patchExportService.OutputDirectory, "backup");
            if (!Directory.Exists(backupDir))
            {
                SettingsStatusMessage = "备份目录不存在，无需清空";
                _toastService.ShowInfo("备份目录不存在，无需清空");
                return;
            }

            var files = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories);
            var deleted = 0;
            foreach (var file in files)
            {
                File.Delete(file);
                deleted++;
            }

            // 清理空子目录
            foreach (var dir in Directory.GetDirectories(backupDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    Directory.Delete(dir);
            }

            AppLogger.Instance.Info($"已清空备份目录，删除 {deleted} 个文件：{backupDir}");
            SettingsStatusMessage = $"已清空备份（删除 {deleted} 个文件）";
            _toastService.ShowSuccess($"已清空备份（删除 {deleted} 个文件）");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "清空备份失败");
            SettingsStatusMessage = $"清空备份失败：{ex.Message}";
            _toastService.ShowError($"清空备份失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportPricesAsync()
    {
        try
        {
            var count = await _patchExportService.ExportPricesCsvAsync(Prices);
            await _patchExportService.ExportEditedPricesJsonAsync(Prices);
            SettingsStatusMessage = $"已导出 {count} 条价格到 {_patchExportService.OutputDirectory}";
            _toastService.ShowSuccess($"导出成功：{count} 条价格");
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "导出价格失败");
            SettingsStatusMessage = $"导出失败：{ex.Message}";
            _toastService.ShowError($"导出失败：{ex.Message}");
        }
    }

    private async Task ExportPatchAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在生成补丁包...";

        try
        {
            var result = await _patchInstaller.ExportPatchZipAsync(Prices, GameDirectory);
            if (result.Success)
            {
                SettingsStatusMessage = $"[{result.GameMode}] 补丁包已生成：{result.InstalledPath}";
                _toastService.ShowSuccess($"[{result.GameMode}] 补丁包生成成功，导出 {result.ExportedCount} 条价格");
            }
            else
            {
                SettingsStatusMessage = $"补丁包生成失败：{result.ErrorMessage}";
                _toastService.ShowError($"补丁包生成失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "生成补丁包失败");
            SettingsStatusMessage = $"生成补丁包失败：{ex.Message}";
            _toastService.ShowError($"生成补丁包失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallPatchAsync()
    {
        IsBusy = true;
        SettingsStatusMessage = "正在生成并安装补丁...";

        try
        {
            var progress = new Progress<string>(msg => _toastService.ShowInfo(msg));
            var result = await _patchInstaller.InstallAsync(Prices, GameDirectory, progress);
            if (result.Success)
            {
                SettingsStatusMessage = $"补丁安装成功，备份：{result.BackupPath}";
                _toastService.ShowSuccess($"补丁安装成功\n导出 {result.ExportedCount} 条价格 · {result.GameMode} · 已备份");
            }
            else
            {
                SettingsStatusMessage = $"补丁安装失败：{result.ErrorMessage}";
                _toastService.ShowError($"补丁安装失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "安装补丁失败");
            SettingsStatusMessage = $"安装补丁失败：{ex.Message}";
            _toastService.ShowError($"安装补丁失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshCategoriesAndFilter()
    {
        var categoryList = Prices
            .Select(p => p.CategoryLabel)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        categoryList.Add("全部");

        Categories = new ObservableCollection<string>(categoryList);
        _selectedCategory = "全部";
        OnPropertyChanged(nameof(SelectedCategory));

        _filteredPrices = new ListCollectionView(Prices);
        _filteredPrices.Filter = FilterBySelectedCategory;
        OnPropertyChanged(nameof(FilteredPrices));

        // DataGrid 在 ItemsSource 切换时会清除新视图的 SortDescriptions，
        // 因此需在 DataGrid 绑定更新后再异步添加默认排序。
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            _filteredPrices.SortDescriptions.Add(new SortDescription(nameof(PoecurrencyItem.PriceExalted), ListSortDirection.Descending));
        }), System.Windows.Threading.DispatcherPriority.DataBind);
    }

    private bool FilterBySelectedCategory(object obj)
    {
        if (obj is not PoecurrencyItem item)
        {
            return false;
        }

        var matchesCategory = string.IsNullOrEmpty(SelectedCategory) || SelectedCategory == "全部" || item.CategoryLabel == SelectedCategory;
        if (!matchesCategory)
        {
            return false;
        }

        if (SelectedCategory != "全部" || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return item.ItemName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

}
