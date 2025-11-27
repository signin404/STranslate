using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Instances;
using STranslate.Plugin;
using STranslate.Resources;
using STranslate.ViewModels.Pages;
using STranslate.Views;
using STranslate.Views.Pages;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace STranslate.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    #region Constructor & DI

    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Internationalization _i18n;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IScreenshot _screenshot;
    private readonly ISnackbar _snakebar;
    private readonly INotification _notification;
    private double _cacheLeft;
    private double _cacheTop;

    public TranslateInstance TranslateInstance { get; }
    public OcrInstance OcrInstance { get; }
    public TtsInstance TtsInstance { get; }
    public VocabularyInstance VocabularyInstance { get; }

    private readonly SqlService _sqlService;

    public Settings Settings { get; }
    public HotkeySettings HotkeySettings { get; }

    public MainWindowViewModel(
        DataProvider dataProvider,
        ILogger<MainWindowViewModel> logger,
        Internationalization i18n,
        IAudioPlayer audioPlayer,
        IScreenshot screenshot,
        ISnackbar snackbar,
        INotification notification,
        TranslateInstance translateInstance,
        OcrInstance ocrInstance,
        TtsInstance ttsInstance,
        VocabularyInstance vocabularyInstance,
        SqlService sqlService,
        Settings settings,
        HotkeySettings hotkeySettings)
    {
        DataProvider = dataProvider;
        _logger = logger;
        _i18n = i18n;
        _audioPlayer = audioPlayer;
        _screenshot = screenshot;
        _snakebar = snackbar;
        _notification = notification;
        TranslateInstance = translateInstance;
        OcrInstance = ocrInstance;
        TtsInstance = ttsInstance;
        VocabularyInstance = vocabularyInstance;
        _sqlService = sqlService;
        Settings = settings;
        HotkeySettings = hotkeySettings;

        _i18n.OnLanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        if (!UACHelper.IsUserAdministrator()) return;

        TrayToolTip = $"{Constant.AppName} # {_i18n.GetTranslation("Administrator")}";
    }

    #endregion

    #region Properties

    private MainWindow MainWindow => (Application.Current.MainWindow as MainWindow)!;
    private bool IsMainWindowVisible => MainWindow.Visibility == Visibility.Visible;

    public DataProvider DataProvider { get; }

    /// <summary>
    /// 等待ContextMenu关闭动画完成的延迟时间（毫秒）
    /// </summary>
    private const int ContextMenuCloseAnimationDelay = 150;

    [ObservableProperty]
    public partial ImageSource TrayIcon { get; set; } = BitmapImageLoc.AppIcon;

    [ObservableProperty]
    public partial string TrayToolTip { get; set; } = Constant.AppName;

    [ObservableProperty]
    public partial bool IsMouseHook { get; set; } = false;

    [ObservableProperty]
    public partial bool IsIdentifyProcessing { get; set; } = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SingleTranslateCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateCommand))]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IdentifiedLanguage { get; set; } = string.Empty;

    public bool IsTopmost
    {
        get => field;
        set
        {
            if (IsMouseHook && !value)
                iNKORE.UI.WPF.Modern.Controls.MessageBox.Show("监听鼠标划词时窗口必须置顶", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                SetProperty(ref field, value);
        }
    }

    public bool CanTranslate => !string.IsNullOrWhiteSpace(InputText);

    #endregion

    #region Translation Commands

    /// <summary>
    /// 执行翻译
    /// </summary>
    /// <param name="text"></param>
    /// <param name="force">不为空则跳过缓存</param>
    public void ExecuteTranslate(string text, string? force = null)
    {
        CancelAllOperations();
        InputText = text;
        TranslateCommand.Execute(force);
        Show();
        UpdateCaret();
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanTranslate))]
    private async Task TranslateAsync(string? force, CancellationToken cancellationToken)
    {
        ResetAllServices();

        IdentifiedLanguage = string.Empty;

        // force 空则优先检查缓存
        var checkCacheFirst = force == null;

        var history = await ExecuteTranslateAsync(checkCacheFirst, cancellationToken);

        if (Settings.HistoryLimit > 0 && history != null)
        {
            // 按服务启用顺序排序
            var enabledServices = TranslateInstance.Services.Where(x => x.IsEnabled).ToList();
            history.Data = [.. history.Data.OrderBy(data => enabledServices.FindIndex(svc => svc.ServiceID.Equals(data.ServiceID)))];
            await _sqlService.InsertOrUpdateDataAsync(history, (long)Settings.HistoryLimit).ConfigureAwait(false);
        }
        else
        {
            // 检查避免重复添加，暂定最大缓存数量为100
            if (_recentTexts.Count >= 100)
                _recentTexts.RemoveAt(_recentTexts.Count - 1);

            if (!_recentTexts.Contains(InputText))
                _recentTexts.Insert(0, InputText);
        }
    }

    [RelayCommand]
    private void TemporaryTranslate(Service service)
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            _snakebar.ShowWarning(_i18n.GetTranslation("InputContentIsEmpty"));
            return;
        }

        if (!SingleTranslateCommand.CanExecute(service))
        {
            _snakebar.ShowWarning(_i18n.GetTranslation("WaitingForPreviousExecution"));
            return;
        }
        service.Options?.TemporaryDisplay = true;

        SingleTranslateCommand.Execute(service);
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanTranslate))]
    private async Task SingleTranslateAsync(Service service, CancellationToken cancellationToken)
    {
        var history = await _sqlService.GetDataAsync(InputText, Settings.SourceLang.ToString(), Settings.TargetLang.ToString());

        if (service.Plugin is IDictionaryPlugin dictionaryPlugin)
        {
            var result = await ExecuteDictAsync(dictionaryPlugin, cancellationToken).ConfigureAwait(false);
            if (result.ResultType == DictionaryResultType.Error)
                return;

            history ??= new HistoryModel
            {
                Time = DateTime.Now,
                SourceText = InputText,
                SourceLang = Settings.SourceLang.ToString(),
                TargetLang = Settings.TargetLang.ToString(),
                Data = []
            };
            // 添加新的历史数据记录并执行字典查询
            history.Data.Add(new(service) { DictResult = result });
            return;
        }

        if (service.Plugin is not ITranslatePlugin plugin || plugin.TransResult.IsProcessing)
            return;

        var (_, source, target) = await LanguageDetector
            .GetLanguageAsync(InputText, cancellationToken, StartProcess, CompleteProcess, FinishProcess)
            .ConfigureAwait(false);

        var translateResult = await ExecuteAsync(plugin, source, target, cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        history ??= new HistoryModel
        {
            Time = DateTime.Now,
            SourceText = InputText,
            SourceLang = Settings.SourceLang.ToString(),
            TargetLang = Settings.TargetLang.ToString(),
            Data = []
        };
        // 添加新的历史数据记录
        var historyData = history.GetData(service);
        if (historyData == null)
        {
            historyData = new HistoryData(service);
            history.Data.Add(historyData);
        }
        historyData.TransResult = translateResult;

        if (service.Options?.AutoBackTranslation ?? false)
        {
            var backResult = await ExecuteBackAsync(plugin, target, source, cancellationToken).ConfigureAwait(false);
            historyData.TransBackResult = backResult;
        }

        if (Settings.HistoryLimit > 0)
        {
            var enabledServices = TranslateInstance.Services.Where(x => x.IsEnabled).ToList();
            history.Data = [.. history.Data.OrderBy(data => enabledServices.FindIndex(svc => svc.ServiceID.Equals(data.ServiceID)))];
            await _sqlService.InsertOrUpdateDataAsync(history, (long)Settings.HistoryLimit).ConfigureAwait(false);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SingleTransBackAsync(Service service, CancellationToken cancellationToken)
    {
        if (service.Plugin is not ITranslatePlugin plugin || plugin.TransBackResult.IsProcessing)
            return;

        var history = await _sqlService.GetDataAsync(InputText, Settings.SourceLang.ToString(), Settings.TargetLang.ToString());

        var (_, source, target) = await LanguageDetector
            .GetLanguageAsync(InputText, cancellationToken, StartProcess, CompleteProcess, FinishProcess)
            .ConfigureAwait(false);

        var backResult = await ExecuteBackAsync(plugin, target, source, cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        history?.GetData(service)?.TransBackResult = backResult;

        if (Settings.HistoryLimit > 0 && history != null)
            await _sqlService.InsertOrUpdateDataAsync(history, (long)Settings.HistoryLimit).ConfigureAwait(false);
    }

    [RelayCommand]
    private void SwapLanguage()
    {
        if (string.IsNullOrWhiteSpace(InputText) ||
            (Settings.SourceLang == Settings.TargetLang && Settings.SourceLang == LangEnum.Auto))
            return;

        (Settings.SourceLang, Settings.TargetLang) = (Settings.TargetLang, Settings.SourceLang);
        TranslateCommand.Execute(null);
    }

    [RelayCommand]
    private void Explain(string text)
    {
        ExecuteTranslate(text);
    }

    #region Translation Execution Logic

    private async Task<HistoryModel?> ExecuteTranslateAsync(bool checkCacheFirst, CancellationToken cancellationToken)
    {
        var enabledSvcs = TranslateInstance.Services.Where(x => x.IsEnabled && x.Options?.ExecMode == ExecutionMode.Automatic).ToList();
        if (enabledSvcs.Count == 0)
            return null;

        HistoryModel? history = null;
        var uncachedSvcs = new List<Service>(enabledSvcs);

        // 尝试从缓存加载
        if (checkCacheFirst && Settings.HistoryLimit > 0)
        {
            history = await _sqlService.GetDataAsync(InputText, Settings.SourceLang.ToString(), Settings.TargetLang.ToString());
            if (history != null)
            {
                IdentifiedLanguage = _i18n.GetTranslation("IdentifiedCache");
                uncachedSvcs = await PopulateResultsFromCacheAsync(history, enabledSvcs, cancellationToken);
            }
        }

        // 如果所有服务都已从缓存加载，则直接返回
        if (uncachedSvcs.Count == 0)
        {
            return history;
        }

        // 对未缓存的服务执行实时翻译
        var (_, source, target) = await LanguageDetector.GetLanguageAsync(InputText, cancellationToken, StartProcess, CompleteProcess, FinishProcess);

        history ??= new HistoryModel
        {
            Time = DateTime.Now,
            SourceText = InputText,
            SourceLang = Settings.SourceLang.ToString(),
            TargetLang = Settings.TargetLang.ToString(),
            Data = []
        };

        await ExecuteTranslationForServicesAsync(uncachedSvcs, source, target, history, cancellationToken);

        return history;
    }

    /// <summary>
    /// 从缓存填充翻译结果，并返回未缓存的服务列表
    /// </summary>
    private async Task<List<Service>> PopulateResultsFromCacheAsync(HistoryModel history, List<Service> services, CancellationToken cancellationToken)
    {
        var uncachedServices = new List<Service>();
        var populateTasks = services.Select(async svc =>
        {
            if (history.GetData(svc) is { } data)
            {
                await PopulateServiceResultFromDataAsync(svc, data);
                if (!history.HasData(svc)) // 检查是否需要反向翻译
                {
                    uncachedServices.Add(svc);
                }
            }
            else
            {
                uncachedServices.Add(svc);
            }
        });
        await Task.WhenAll(populateTasks);
        return uncachedServices;
    }

    /// <summary>
    /// 根据历史数据填充单个服务的结果
    /// </summary>
    private async Task PopulateServiceResultFromDataAsync(Service svc, HistoryData data)
    {
        if (svc.Plugin is ITranslatePlugin tPlugin)
        {
            if (data.TransResult != null && data.TransResult.IsSuccess && !string.IsNullOrWhiteSpace(data.TransResult.Text))
                tPlugin.TransResult.Update(data.TransResult);

            if ((svc.Options?.AutoBackTranslation ?? false) && data.TransBackResult != null && data.TransBackResult.IsSuccess && !string.IsNullOrWhiteSpace(data.TransBackResult.Text))
                tPlugin.TransBackResult.Update(data.TransBackResult);
        }
        else if (svc.Plugin is IDictionaryPlugin dPlugin)
        {
            if (data.DictResult != null && data.DictResult.ResultType != DictionaryResultType.Error && data.DictResult.ResultType != DictionaryResultType.None)
            {
                dPlugin.DictionaryResult.Update(data.DictResult);
            }
        }
    }

    /// <summary>
    /// 为指定的服务列表执行翻译
    /// </summary>
    private async Task ExecuteTranslationForServicesAsync(IEnumerable<Service> services, LangEnum source, LangEnum target, HistoryModel history, CancellationToken cancellationToken)
    {
        var maxConcurrency = Math.Min(services.Count(), Environment.ProcessorCount * 10);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var translateTasks = services.Select(svc =>
            ExecuteTranslationHandlerAsync(svc, source, target, semaphore, history, cancellationToken));

        try
        {
            await Task.WhenAll(translateTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
    }

    private async Task ExecuteTranslationHandlerAsync(Service svc, LangEnum source, LangEnum target,
        SemaphoreSlim semaphore, HistoryModel history, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (svc.Plugin)
            {
                case ITranslatePlugin translatePlugin:
                    await ProcessTranslatePluginAsync(svc, translatePlugin, source, target, history, cancellationToken).ConfigureAwait(false);
                    break;
                case IDictionaryPlugin dictionaryPlugin:
                    await ProcessDictionaryPluginAsync(svc, dictionaryPlugin, history, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ProcessTranslatePluginAsync(Service service, ITranslatePlugin plugin, LangEnum source, LangEnum target,
        HistoryModel history, CancellationToken cancellationToken)
    {
        // 如果历史记录中没有该服务的数据，则执行全新翻译
        if (history.GetData(service) == null)
        {
            await ExecuteNewTranslationAsync(service, plugin, source, target, history, cancellationToken).ConfigureAwait(false);
        }
        // 否则，只执行反向翻译（如果需要）
        else if ((service.Options?.AutoBackTranslation ?? false) && history.GetData(service)?.TransBackResult == null)
        {
            await ExecuteBackTranslationOnlyAsync(service, plugin, target, source, history, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteNewTranslationAsync(Service service, ITranslatePlugin plugin, LangEnum source, LangEnum target,
        HistoryModel history, CancellationToken cancellationToken)
    {
        // 执行主翻译
        var translateResult = await ExecuteAsync(plugin, source, target, cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        // 添加新的历史数据记录
        var historyData = new HistoryData(service);
        history.Data.Add(historyData);
        historyData.TransResult = translateResult;

        // 执行反向翻译（如果需要且主翻译成功）
        if (service.Options?.AutoBackTranslation ?? false)
        {
            var backResult = await ExecuteBackAsync(plugin, target, source, cancellationToken).ConfigureAwait(false);
            historyData.TransBackResult = backResult;
        }
    }

    private async Task ExecuteBackTranslationOnlyAsync(Service service, ITranslatePlugin plugin, LangEnum target, LangEnum source,
        HistoryModel history, CancellationToken cancellationToken)
    {
        var backResult = await ExecuteBackAsync(plugin, target, source, cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        var historyData = history.GetData(service);
        historyData?.TransBackResult = backResult;
    }

    private async Task ProcessDictionaryPluginAsync(Service service, IDictionaryPlugin plugin,
        HistoryModel history, CancellationToken cancellationToken)
    {
        // 如果缓存中已存在数据则跳过
        if (history.HasData(service))
            return;

        var result = await ExecuteDictAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (result.ResultType == DictionaryResultType.Error)
            return;

        // 添加新的历史数据记录并执行字典查询
        var historyData = new HistoryData(service);
        history.Data.Add(historyData);
        historyData.DictResult = result;
    }

    private async Task<DictionaryResult> ExecuteDictAsync(IDictionaryPlugin plugin, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.Reset();
            plugin.DictionaryResult.IsProcessing = true;
            await plugin.TranslateAsync(InputText, plugin.DictionaryResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plugin.DictionaryResult.ResultType = DictionaryResultType.Error;
            plugin.DictionaryResult.Text = _i18n.GetTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            plugin.DictionaryResult.ResultType = DictionaryResultType.Error;
            plugin.DictionaryResult.Text = $"{_i18n.GetTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            if (plugin.DictionaryResult.ResultType != DictionaryResultType.NoResult)
                plugin.DictionaryResult.Duration = DateTime.Now - startTime;
            if (plugin.DictionaryResult.IsProcessing)
                plugin.DictionaryResult.IsProcessing = false;
        }

        return plugin.DictionaryResult;
    }

    private async Task<TranslateResult> ExecuteAsync(ITranslatePlugin plugin, LangEnum source, LangEnum target, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.Reset();
            plugin.TransResult.IsProcessing = true;
            await plugin.TranslateAsync(new TranslateRequest(InputText, source, target), plugin.TransResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plugin.TransResult.IsSuccess = false;
            plugin.TransResult.Text = _i18n.GetTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            plugin.TransResult.IsSuccess = false;
            plugin.TransResult.Text = $"{_i18n.GetTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            plugin.TransResult.Duration = DateTime.Now - startTime;
            if (plugin.TransResult.IsProcessing)
                plugin.TransResult.IsProcessing = false;
        }

        return plugin.TransResult;
    }

    private async Task<TranslateResult> ExecuteBackAsync(ITranslatePlugin plugin, LangEnum target, LangEnum source, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.ResetBack();
            plugin.TransBackResult.IsProcessing = true;
            await plugin.TranslateAsync(new TranslateRequest(plugin.TransResult.Text, target, source), plugin.TransBackResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plugin.TransBackResult.IsSuccess = false;
            plugin.TransBackResult.Text = _i18n.GetTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            plugin.TransBackResult.IsSuccess = false;
            plugin.TransBackResult.Text = $"{_i18n.GetTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            plugin.TransBackResult.Duration = DateTime.Now - startTime;
            if (plugin.TransBackResult.IsProcessing)
                plugin.TransBackResult.IsProcessing = false;
        }

        return plugin.TransBackResult;
    }

    #endregion

    #endregion

    #region OCR & Screenshot Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScreenshotTranslateAsync(CancellationToken cancellationToken)
    {
        var ocrPlugin = GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        if (Settings.ScreenshotTranslateInImage && TranslateInstance.ImageTranslateService == null)
        {
            _notification.ShowWithButton(
                 "无法获取图片翻译服务",
                 "点击前往",
                 () =>
                 {
                     Application.Current.Dispatcher.Invoke(() =>
                     {
                         SingletonWindowOpener
                             .Open<SettingsWindow>()
                             .Activate();

                         Application.Current.Windows
                                 .OfType<SettingsWindow>()
                                 .First()
                                 .Navigate(nameof(TranslatePage));
                     });
                 },
                 "当前未配置启用图片翻译服务，请先前往「设置-服务-文本翻译」配置后使用该功能");
            return;
        }

        using var bitmap = await _screenshot.GetScreenshotAsync();
        await ScreenshotTranslateHandlerAsync(bitmap, ocrPlugin, cancellationToken);
    }

    public async Task ScreenshotTranslateHandlerAsync(System.Drawing.Bitmap? bitmap, IOcrPlugin? ocrPlugin = default, CancellationToken cancellationToken = default)
    {
        if (bitmap == null) return;

        ocrPlugin ??= GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        if (Settings.ScreenshotTranslateInImage)
        {
            var window = await SingletonWindowOpener.OpenAsync<ImageTranslateWindow>();
            await ((ImageTranslateWindowViewModel)window.DataContext).ExecuteCommand.ExecuteAsync(bitmap);
            return;
        }

        try
        {
            CursorHelper.Execute();
            var data = Utilities.ToBytes(bitmap);
            var result = await ocrPlugin.RecognizeAsync(new OcrRequest(data, LangEnum.Auto), cancellationToken);

            if (!result.IsSuccess || string.IsNullOrEmpty(result.Text))
                return;

            ExecuteTranslate(Utilities.LinebreakHandler(result.Text, Settings.LineBreakHandleType));
        }
        catch (TaskCanceledException)
        {
            //TODO: 考虑提示用户取消操作
        }
        catch (Exception ex)
        {
            _notification.Show(_i18n.GetTranslation("Prompt"), $"{_i18n.GetTranslation("OcrFailed")}\n{ex.Message}");
            _logger.LogError(ex, "OCR execution failed");
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    [RelayCommand]
    private async Task OcrAsync()
    {
        if (GetOcrSvcAndNotify() == null)
            return;

        using var bitmap = await _screenshot.GetScreenshotAsync();
        await OcrHandlerAsync(bitmap);
    }

    public async Task OcrHandlerAsync(System.Drawing.Bitmap? bitmap)
    {
        if (bitmap == null) return;
        var window = await SingletonWindowOpener.OpenAsync<OcrWindow>();
        await ((OcrWindowViewModel)window.DataContext).ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SilentOcrAsync(CancellationToken cancellationToken)
    {
        var ocrPlugin = GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        using var bitmap = await _screenshot.GetScreenshotAsync();
        await SilentOcrHandlerAsync(bitmap, ocrPlugin, cancellationToken);
    }

    public async Task SilentOcrHandlerAsync(System.Drawing.Bitmap? bitmap, IOcrPlugin? ocrPlugin = default, CancellationToken cancellationToken = default)
    {
        if (bitmap == null) return;

        ocrPlugin ??= GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;
        try
        {
            CursorHelper.Execute();
            var data = Utilities.ToBytes(bitmap);
            var result = await ocrPlugin.RecognizeAsync(new OcrRequest(data, LangEnum.Auto), cancellationToken);
            if (result.IsSuccess && !string.IsNullOrEmpty(result.Text))
            {
                Utilities.SetText(result.Text);
            }
        }
        catch (TaskCanceledException)
        {
            //TODO: 考虑提示用户取消操作
        }
        catch (Exception ex)
        {
            _notification.Show(_i18n.GetTranslation("Prompt"), $"{_i18n.GetTranslation("OcrFailed")}\n{ex.Message}");
            _logger.LogError(ex, "OCR execution failed");
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    private IOcrPlugin? GetOcrSvcAndNotify()
    {
        var svc = OcrInstance.GetActiveSvc<IOcrPlugin>();
        if (svc == null)
        {
            _notification.ShowWithButton(
                "无法获取OCR服务",
                "点击前往",
                () =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        SingletonWindowOpener
                            .Open<SettingsWindow>()
                            .Activate();

                        Application.Current.Windows
                                .OfType<SettingsWindow>()
                                .First()
                                .Navigate(nameof(OcrPage));
                    });
                },
                "当前未配置或者启用OCR服务，请先前往「设置-服务-文本识别」配置后使用该功能");
            return default;
        }

        return svc;
    }

    #endregion

    #region TTS & Audio Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PlayAudioAsync(string text, CancellationToken cancellationToken)
    {
        var ttsSvc = TtsInstance.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
            return;

        await ttsSvc.PlayAudioAsync(text, cancellationToken);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PlayAudioUrlAsync(string url, CancellationToken cancellationToken)
        => await _audioPlayer.PlayAsync(url, cancellationToken);

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SilentTtsAsync(CancellationToken cancellationToken)
    {
        var ttsSvc = TtsInstance.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
            return;

        var (success, text) = await GetTextAsync();
        if (!success || string.IsNullOrWhiteSpace(text))
            return;
        await SilentTtsHandlerAsync(text, ttsSvc, cancellationToken);
    }

    public async Task SilentTtsHandlerAsync(string text, ITtsPlugin? ttsSvc = default, CancellationToken cancellationToken = default)
    {
        ttsSvc ??= TtsInstance.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
            return;

        try
        {
            CursorHelper.Execute();
            await ttsSvc.PlayAudioAsync(text, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // Ignore
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    #endregion

    #region Voculary Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveToVocabularyAsync(string text, CancellationToken cancellationToken)
    {
        var vocabularySvc = VocabularyInstance.GetActiveSvc<IVocabularyPlugin>();
        if (vocabularySvc == null)
            return;

        var result = await vocabularySvc.SaveAsync(text, cancellationToken);
        if (result.IsSuccess)
            _snakebar.ShowSuccess(_i18n.GetTranslation("OperationSuccess"));
        else
            _snakebar.ShowError(_i18n.GetTranslation("OperationFailed"));
    }

    #endregion

    #region History Commands

    [RelayCommand]
    private async Task HistoryPreviousAsync()
    {
        var result = Settings.HistoryLimit == HistoryLimit.NotSave ?
            await QueryRecentTextFromCacheAsync() :
            await QueryRecentTextFromHistoryAsync();

        if (!string.IsNullOrWhiteSpace(result))
            ExecuteTranslate(result);
        else
            _snakebar.ShowWarning(_i18n.GetTranslation("NavigateFailed"));
    }

    [RelayCommand]
    private async Task HistoryNextAsync()
    {
        var result = Settings.HistoryLimit == HistoryLimit.NotSave ?
            await QueryRecentTextFromCacheAsync(isNext: true) :
            await QueryRecentTextFromHistoryAsync(isNext: true);

        if (!string.IsNullOrWhiteSpace(result))
            ExecuteTranslate(result);
        else
            _snakebar.ShowWarning(_i18n.GetTranslation("NavigateFailed"));
    }

    private List<string> _recentTexts = [];

    private async Task<string?> QueryRecentTextFromCacheAsync(bool isNext = false)
    {
        if (_recentTexts.Count == 0)
            return default;

        if (string.IsNullOrWhiteSpace(InputText))
        {
            // 如果输入为空，则获取最新的一条历史记录
            return _recentTexts[0];
        }
        else
        {
            var currentIndex = _recentTexts.FindIndex(t => t.Equals(InputText, StringComparison.OrdinalIgnoreCase));
            if (currentIndex == -1)
                return default;
            var newIndex = isNext ? currentIndex - 1 : currentIndex + 1;
            if (newIndex < 0 || newIndex >= _recentTexts.Count)
                return default;
            return _recentTexts[newIndex];
        }
    }

    private async Task<string?> QueryRecentTextFromHistoryAsync(bool isNext = false)
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            // 如果输入为空，则获取最新的一条历史记录
            var histories = await _sqlService.GetDataAsync(1, 1);
            return histories?.FirstOrDefault()?.SourceText;
        }
        else
        {
            // 否则，获取当前输入文本对应的历史记录
            var current = await _sqlService.GetDataAsync(InputText, Settings.SourceLang.ToString(), Settings.TargetLang.ToString());
            if (current != null)
            {
                var history = isNext ? await _sqlService.GetNextAsync(current) : await _sqlService.GetPreviousAsync(current);
                return history?.SourceText;
            }
        }

        return default;
    }

    #endregion

    #region Mouse Hook Feature

    [RelayCommand]
    private void ToggleMouseHookTranslate() => IsMouseHook = !IsMouseHook;

    private async Task ToggleMouseHookAsync(bool enable)
    {
        if (enable)
        {
            Show();
            IsTopmost = true;
            await Utilities.StartMouseTextSelectionAsync();
            Utilities.MouseTextSelected += OnMouseTextSelected;
        }
        else
        {
            IsTopmost = false;
            Utilities.StopMouseTextSelection();
            Utilities.MouseTextSelected -= OnMouseTextSelected;
        }
    }

    private void OnMouseTextSelected(string text)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ExecuteTranslate(Utilities.LinebreakHandler(text, Settings.LineBreakHandleType));
        });
    }

    [RelayCommand]
    private async Task CrosswordTranslateAsync()
    {
        var (success, text) = await GetTextAsync();
        if (success && !string.IsNullOrWhiteSpace(text))
        {
            ExecuteTranslate(Utilities.LinebreakHandler(text, Settings.LineBreakHandleType));
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ReplaceTranslateAsync(CancellationToken cancellationToken)
    {
        if (TranslateInstance.ReplaceService?.Plugin is not ITranslatePlugin transPlugin)
        {
            _notification.ShowWithButton(
                "无法获取替换翻译服务",
                "点击前往",
                () =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        SingletonWindowOpener
                            .Open<SettingsWindow>()
                            .Activate();

                        Application.Current.Windows
                                .OfType<SettingsWindow>()
                                .First()
                                .Navigate(nameof(TranslatePage));
                    });
                },
                "当前未配置启用替换翻译服务，请先前往「设置-服务-文本翻译」配置后使用该功能");

            return;
        }

        try
        {
            CursorHelper.Execute();
            var (success, text) = await GetTextAsync();
            if (!success || string.IsNullOrWhiteSpace(text)) return;

            var (isSuccess, source, target) = await LanguageDetector.GetLanguageAsync(text, cancellationToken).ConfigureAwait(false);
            if (!isSuccess)
            {
                _logger.LogWarning($"Language detection failed for text: {text}");
                _notification.Show(_i18n.GetTranslation("Prompt"), "语言检测失败");
            }
            var result = new TranslateResult();
            await transPlugin.TranslateAsync(new TranslateRequest(text, source, target), result, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess && !string.IsNullOrEmpty(result.Text))
                InputHelper.PrintText(result.Text);
            else
                throw new Exception($"IsSuccess: {result.IsSuccess}, Text: {result.Text}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "替换翻译失败");
            CursorHelper.Error();
            await Task.Delay(1000);
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    #endregion

    #region Window & UI Control Commands

    [RelayCommand]
    private void ResetLocation()
    {
        var screen = SelectedScreen();
        Settings.MainWindowLeft = HorizonCenter(screen);
        Settings.MainWindowTop = VerticalCenter(screen);
        Show();
    }

    public void Show()
    {
        if (Settings.MainWindowLeft <= -18000 && Settings.MainWindowTop <= -18000)
        {
            Settings.MainWindowLeft = _cacheLeft;
            Settings.MainWindowTop = _cacheTop;
        }
        MainWindow.Visibility = Visibility.Visible;
        UpdatePosition();
        MainWindow.Activate();
        MainWindow.PART_Input.Focus();
    }

    public void Hide() => MainWindow.Visibility = Visibility.Collapsed;

    [RelayCommand]
    private void ToggleApp()
    {
        if (IsMainWindowVisible && !IsTopmost)
            Hide();
        else
            Show();
    }

    [RelayCommand]
    private void Cancel(Window window)
    {
        if (!IsMouseHook)
        {
            if (IsTopmost) IsTopmost = false;
            window.Visibility = Visibility.Collapsed;
        }
        CancelAllOperations();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync(object? parameter)
    {
        // 如果由 ContextMenu 触发，等待关闭动画完成
        if (parameter is not null)
            await Task.Delay(ContextMenuCloseAnimationDelay);

        // 如果从主窗口打开设置，主动隐藏主窗口
        if (MainWindow.IsActive && IsMainWindowVisible && !IsTopmost)
            Hide();

        await SingletonWindowOpener.OpenAsync<SettingsWindow>();
    }

    [RelayCommand]
    private async Task OpenHistoryAsync()
    {
        await OpenSettingsAsync(null);
        Application.Current.Windows
                    .OfType<SettingsWindow>()
                    .First()
                    .Navigate(nameof(HistoryPage));
    }

    [RelayCommand]
    private async Task NavigateAsync(Service service)
    {
        await OpenSettingsAsync(string.Empty);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Application.Current.Windows
                    .OfType<SettingsWindow>()
                    .First()
                    .Navigate(nameof(TranslatePage));

            Ioc.Default.GetRequiredService<TranslateViewModel>().SelectedItem = service;
        }, DispatcherPriority.Normal);
    }

    [RelayCommand]
    private void CloseService(Service service) => service.IsEnabled = false;

    [RelayCommand]
    private void ToggleTopmost() => IsTopmost = !IsTopmost;

    [RelayCommand]
    private void ToggleHideInput() => Settings.HideInput = !Settings.HideInput;

    [RelayCommand]
    private void ChangeColorScheme()
    {
        var current = Settings.ColorScheme;
        var next = current + 1;
        if (next > ElementTheme.Dark) next = 0;
        Settings.ColorScheme = next;
    }

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    #endregion

    #region Text & Clipboard Manipulation

    [RelayCommand]
    private void InputClear()
    {
        CancelAllOperations();
        InputText = string.Empty;

        ResetAllServices();
        Show();
    }

    [RelayCommand]
    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Utilities.SetText(text);
        _snakebar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
    }

    [RelayCommand]
    private async Task InsertAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (IsTopmost) IsTopmost = false;
        MainWindow.Visibility = Visibility.Collapsed;
        await Task.Delay(150);
        InputHelper.PrintText(text);
    }

    [RelayCommand]
    private void RemoveLineBreaks(TextBox textBox) =>
        Utilities.TransformText(
            textBox,
            t => t.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "),
            () => TranslateCommand.Execute(null));

    [RelayCommand]
    private void RemoveSpaces(TextBox textBox) =>
        Utilities.TransformText(
            textBox,
            t => t.Replace(" ", ""),
            () => TranslateCommand.Execute(null));

    [RelayCommand]
    private void CleanTransBack(ITranslatePlugin plugin) => plugin.ResetBack();

    #endregion

    #region Window Position

    public void UpdatePosition(bool hideOnStartup = false)
    {
        if (IsTopmost) return;

        InternalUpdatePosition(hideOnStartup);
        InternalUpdatePosition(hideOnStartup);

        void InternalUpdatePosition(bool hideOnStartup)
        {
            if (hideOnStartup)
            {
                // 隐藏时缓存位置，第一次打开时恢复位置
                if (Settings.WindowScreen == WindowScreenType.RememberLastLaunchLocation &&
                    _cacheLeft == 0 && _cacheTop == 0)
                {
                    _cacheLeft = Settings.MainWindowLeft;
                    _cacheTop = Settings.MainWindowTop;
                }
                Settings.MainWindowLeft = -18000;
                Settings.MainWindowTop = -18000;
                return;
            }
            if (Settings.WindowScreen == WindowScreenType.RememberLastLaunchLocation)
            {
                var previousScreenWidth = Settings.PreviousScreenWidth;
                var previousScreenHeight = Settings.PreviousScreenHeight;
                GetDpi(out var previousDpiX, out var previousDpiY);

                Settings.PreviousScreenWidth = SystemParameters.VirtualScreenWidth;
                Settings.PreviousScreenHeight = SystemParameters.VirtualScreenHeight;
                GetDpi(out var currentDpiX, out var currentDpiY);

                if (previousScreenWidth != 0 && previousScreenHeight != 0 &&
                    previousDpiX != 0 && previousDpiY != 0 &&
                    (previousScreenWidth != SystemParameters.VirtualScreenWidth ||
                     previousScreenHeight != SystemParameters.VirtualScreenHeight ||
                     previousDpiX != currentDpiX || previousDpiY != currentDpiY))
                {
                    AdjustPositionForResolutionChange();
                    return;
                }

                Settings.MainWindowLeft = Settings.MainWindowLeft;
                Settings.MainWindowTop = Settings.MainWindowTop;
            }
            else
            {
                var screen = SelectedScreen();
                switch (Settings.WindowAlign)
                {
                    case WindowAlignType.Center:
                        Settings.MainWindowLeft = HorizonCenter(screen);
                        Settings.MainWindowTop = VerticalCenter(screen);
                        break;
                    case WindowAlignType.CenterTop:
                        Settings.MainWindowLeft = HorizonCenter(screen);
                        Settings.MainWindowTop = VerticalTop(screen);
                        break;
                    case WindowAlignType.LeftTop:
                        Settings.MainWindowLeft = HorizonLeft(screen);
                        Settings.MainWindowTop = VerticalTop(screen);
                        break;
                    case WindowAlignType.RightTop:
                        Settings.MainWindowLeft = HorizonRight(screen);
                        Settings.MainWindowTop = VerticalTop(screen);
                        break;
                    case WindowAlignType.Custom:
                        var customLeft = Win32Helper.TransformPixelsToDIP(MainWindow,
                            screen.WorkingArea.X + Settings.CustomWindowLeft, 0);
                        var customTop = Win32Helper.TransformPixelsToDIP(MainWindow, 0,
                            screen.WorkingArea.Y + Settings.CustomWindowTop);
                        Settings.MainWindowLeft = customLeft.X;
                        Settings.MainWindowTop = customTop.Y;
                        break;
                }
            }
        }
    }

    private void AdjustPositionForResolutionChange()
    {
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        GetDpi(out var currentDpiX, out var currentDpiY);

        var previousLeft = Settings.MainWindowLeft;
        var previousTop = Settings.MainWindowTop;
        GetDpi(out var previousDpiX, out var previousDpiY);

        var widthRatio = screenWidth / Settings.PreviousScreenWidth;
        var heightRatio = screenHeight / Settings.PreviousScreenHeight;
        var dpiXRatio = currentDpiX / previousDpiX;
        var dpiYRatio = currentDpiY / previousDpiY;

        var newLeft = previousLeft * widthRatio * dpiXRatio;
        var newTop = previousTop * heightRatio * dpiYRatio;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;

        var maxX = screenLeft + screenWidth - MainWindow.ActualWidth;
        var maxY = screenTop + screenHeight - MainWindow.ActualHeight;

        Settings.MainWindowLeft = Math.Max(screenLeft, Math.Min(newLeft, maxX));
        Settings.MainWindowTop = Math.Max(screenTop, Math.Min(newTop, maxY));
    }

    private void GetDpi(out double dpiX, out double dpiY)
    {
        var source = PresentationSource.FromVisual(MainWindow);
        if (source != null && source.CompositionTarget != null)
        {
            var matrix = source.CompositionTarget.TransformToDevice;
            dpiX = 96 * matrix.M11;
            dpiY = 96 * matrix.M22;
        }
        else
        {
            dpiX = 96;
            dpiY = 96;
        }
    }

    private MonitorInfo SelectedScreen()
    {
        MonitorInfo screen;
        switch (Settings.WindowScreen)
        {
            case WindowScreenType.Cursor:
                screen = MonitorInfo.GetCursorDisplayMonitor();
                break;
            case WindowScreenType.Focus:
                screen = MonitorInfo.GetNearestDisplayMonitor(Win32Helper.GetForegroundWindow());
                break;
            case WindowScreenType.Primary:
                screen = MonitorInfo.GetPrimaryDisplayMonitor();
                break;
            case WindowScreenType.Custom:
                var allScreens = MonitorInfo.GetDisplayMonitors();
                if (Settings.CustomScreenNumber <= allScreens.Count)
                    screen = allScreens[Settings.CustomScreenNumber - 1];
                else
                    screen = allScreens[0];
                break;
            default:
                screen = MonitorInfo.GetDisplayMonitors()[0];
                break;
        }

        return screen ?? MonitorInfo.GetDisplayMonitors()[0];
    }

    private double HorizonCenter(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, 0);
        var dip2 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.Width, 0);
        var left = (dip2.X - MainWindow.ActualWidth) / 2 + dip1.X;
        return left;
    }

    private double VerticalCenter(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, 0, screen.WorkingArea.Y);
        var dip2 = Win32Helper.TransformPixelsToDIP(MainWindow, 0, screen.WorkingArea.Height);
        var top = (dip2.Y - MainWindow.PART_Input.ActualHeight) / 4 + dip1.Y;
        return top;
    }

    private double HorizonRight(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, 0);
        var dip2 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.Width, 0);
        var left = (dip1.X + dip2.X - MainWindow.ActualWidth) - 10;
        return left;
    }

    private double HorizonLeft(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, 0);
        var left = dip1.X + 10;
        return left;
    }

    private double VerticalTop(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, 0, screen.WorkingArea.Y);
        var top = dip1.Y + 10;
        return top;
    }

    #endregion

    #region Global Hotkeys

    public void ToggleGlobalHotkey() => Settings.DisableGlobalHotkeys = !Settings.DisableGlobalHotkeys;

    #endregion

    #region Helpers & Event Handlers

    partial void OnInputTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(IdentifiedLanguage))
            IdentifiedLanguage = string.Empty;
    }

    partial void OnIsMouseHookChanged(bool value) => _ = ToggleMouseHookAsync(value);

    private void UpdateCaret()
    {
        MainWindow.PART_Input.SetCaretIndex(InputText.Length);
    }

    private void ResetAllServices()
    {
        var services = TranslateInstance.Services.Where(x => x.IsEnabled).ToList();
        foreach (var service in services)
        {
            service.Options?.TemporaryDisplay = false;
            if (service.Plugin is ITranslatePlugin tPlugin) tPlugin.Reset();
            else if (service.Plugin is IDictionaryPlugin dPlugin) dPlugin.Reset();
        }
    }

    private void CancelAllOperations()
    {
        SingleTranslateCancelCommand.Execute(null);
        SingleTransBackCancelCommand.Execute(null);
        TranslateCancelCommand.Execute(null);
        PlayAudioCancelCommand.Execute(null);
        PlayAudioUrlCancelCommand.Execute(null);
        ScreenshotTranslateCancelCommand.Execute(null);
        SaveToVocabularyCancelCommand.Execute(null);
    }

    private async Task<(bool success, string text)> GetTextAsync()
    {
        try
        {
            var text = await Utilities.GetSelectedTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("取词失败，可能：未选中文本、文本禁止复制、取词间隔过短、文本所属软件权限高于本软件");
                _notification.Show("未识别到文本", "请确保选中要翻译的文本\n若问题仍然存在请尝试以管理员权限重启软件");
                return (false, string.Empty);
            }
            return (true, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取剪贴板异常请重试");
            return (false, string.Empty);
        }
    }

    private void StartProcess()
    {
        IdentifiedLanguage = string.Empty;
        IsIdentifyProcessing = true;
    }
    private void FinishProcess() => IsIdentifyProcessing = false;
    private void CompleteProcess(bool isSuccess, LangEnum source)
    {
        var suffix = isSuccess ? string.Empty : $"「{_i18n.GetTranslation("UseSettingLang")}」";
        IdentifiedLanguage = _i18n.GetTranslation($"LangEnum{source}") + suffix;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Utilities.MouseTextSelected -= OnMouseTextSelected;

        // 如果窗口一直没打开过，恢复位置后再退出
        if (Settings.MainWindowLeft <= -18000 && Settings.MainWindowTop <= -18000)
        {
            Settings.MainWindowLeft = _cacheLeft;
            Settings.MainWindowTop = _cacheTop;
            Settings.Save();
        }

        _i18n.OnLanguageChanged -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }

    #endregion
}