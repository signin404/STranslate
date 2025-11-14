using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using iNKORE.UI.WPF.Modern;
using Serilog.Core;
using Serilog.Events;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Views;
using System.Windows.Media;

namespace STranslate.Core;

public partial class Settings : ObservableObject
{
    private AppStorage<Settings> Storage { get; set; } = null!;

    #region Setting Items

    [ObservableProperty] public partial bool AutoStartup { get; set; } = false;
    [ObservableProperty] public partial StartMode StartMode { get; set; } = StartMode.Normal;

    [ObservableProperty] public partial string AppFont { get; set; } = Win32Helper.GetSystemDefaultFont();

    [ObservableProperty] public partial string Language { get; set; } = Constant.SystemLanguageCode;

    [ObservableProperty] public partial bool HideOnStartup { get; set; } = false;

    [ObservableProperty] public partial bool HideWhenDeactivated { get; set; } = true;

    [ObservableProperty] public partial bool DisableGlobalHotkeys { get; set; } = false;

    [ObservableProperty] public partial bool IgnoreHotkeysOnFullscreen { get; set; } = false;

    [ObservableProperty] public partial ElementTheme ColorScheme { get; set; }

    [ObservableProperty] public partial bool IsColorSchemeVisible { get; set; } = true;

    [ObservableProperty] public partial bool ScreenshotTranslateInImage { get; set; } = true;

    [ObservableProperty] public partial bool IsScreenshotTranslateInImageVisible { get; set; } = true;

    [ObservableProperty] public partial bool HideInput { get; set; } = false;

    [ObservableProperty] public partial bool HideInputWithLangSelectControl { get; set; } = false;

    [ObservableProperty] public partial bool IsHideInputVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsMouseHookVisible { get; set; } = true;

    [ObservableProperty] public partial int HttpTimeout { get; set; } = 30;

    [ObservableProperty] public partial LangEnum SourceLang { get; set; } = LangEnum.Auto;

    [ObservableProperty] public partial LangEnum TargetLang { get; set; } = LangEnum.Auto;

    /// <summary>
    ///     语种识别类型
    /// </summary>
    [ObservableProperty] public partial LanguageDetectorType LanguageDetector { get; set; } = LanguageDetectorType.Local;

    /// <summary>
    ///     原始语言识别为自动时使用该配置
    ///     * 使用在线识别服务出错时使用
    /// </summary>
    [ObservableProperty] public partial LangEnum SourceLangIfAuto { get; set; } = LangEnum.English;

    [ObservableProperty] public partial LangEnum FirstLanguage { get; set; } = LangEnum.ChineseSimplified;

    [ObservableProperty] public partial LangEnum SecondLanguage { get; set; } = LangEnum.English;

    /// <summary>
    /// 粘贴时自动翻译
    /// </summary>
    [ObservableProperty] public partial bool TranslateOnPaste { get; set; } = true;

    public double PreviousScreenWidth { get; set; }
    public double PreviousScreenHeight { get; set; }
    [ObservableProperty] public partial int CustomScreenNumber { get; set; } = 1;
    [ObservableProperty] public partial WindowScreenType WindowScreen { get; set; } = WindowScreenType.Cursor;
    [ObservableProperty] public partial WindowAlignType WindowAlign { get; set; } = WindowAlignType.Center;
    [ObservableProperty] public partial double MainWindowLeft { get; set; }
    [ObservableProperty] public partial double MainWindowTop { get; set; }
    [ObservableProperty] public partial double CustomWindowLeft { get; set; }
    [ObservableProperty] public partial double CustomWindowTop { get; set; }

    private double _mainWindowWidth = 470;
    public double MainWindowWidth
    {
        get => _mainWindowWidth;
        set
        {
            // 隐藏时这个宽度似乎会变化
            if (App.Current.MainWindow != null && !App.Current.MainWindow.IsVisible) return;
            SetProperty(ref _mainWindowWidth, value);
        }
    }
    [ObservableProperty] public partial double MainWindowMaxHeight { get; set; } = 800;

    /// <summary>
    /// 输入输出框字体大小
    /// </summary>
    [ObservableProperty] public partial double TextFontSize { get; set; } = 14;

    /// <summary>
    /// 主界面Llm服务是否显示提示词按钮
    /// </summary>
    [ObservableProperty] public partial bool ShowPromptButton { get; set; } = true;

    /// <summary>
    /// 取词时换行处理
    /// </summary>
    [ObservableProperty] public partial LineBreakHandleType LineBreakHandleType { get; set; } = LineBreakHandleType.RemoveExtraLineBreak;

    #region Layout Analysis
    /* 版面分析参数配置
     *
     * 1.全部合并
     * private const double VerticalThresholdRatio = 6.0;
     * private const double HorizontalThresholdRatio = 6.0;
     * private const double LineSpacingThresholdRatio = 0.3;
     * private const double WordSpacingThresholdRatio = 0.2;
     *
     * 2.严格段落分析（保持原始结构）
     * private const double VerticalThresholdRatio = 0.3;
     * private const double HorizontalThresholdRatio = 0.2;
     * private const double LineSpacingThresholdRatio = 0.2;
     * private const double WordSpacingThresholdRatio = 0.1;
     *
     * 3.标准文档分析（推荐默认值）
     * private const double VerticalThresholdRatio = 0.8;
     * private const double HorizontalThresholdRatio = 0.5;
     * private const double LineSpacingThresholdRatio = 0.3;
     * private const double WordSpacingThresholdRatio = 0.2;
     *
     * 4.分栏文档分析（合并左右分栏）
     * private const double VerticalThresholdRatio = 0.6;
     * private const double HorizontalThresholdRatio = 2.0;     // 增大以跨越分栏间距
     * private const double LineSpacingThresholdRatio = 0.3;
     * private const double WordSpacingThresholdRatio = 0.2;
     *
     * 5.不合并任何文本块
     * private const double VerticalThresholdRatio = 0.0;       // 设置为0，不允许垂直合并
     * private const double HorizontalThresholdRatio = 0.0;     // 设置为0，不允许水平合并
     * private const double LineSpacingThresholdRatio = 0.3;    // 这两个参数在不合并时不会被使用
     * private const double WordSpacingThresholdRatio = 0.2;    // 这两个参数在不合并时不会被使用
     */
    [ObservableProperty] public partial LayoutAnalysisMode LayoutAnalysisMode { get; set; } = LayoutAnalysisMode.StandardDocument;

    /// <summary>
    /// 垂直相邻检测阈值比例
    /// 作用: 控制上下相邻文本块的合并敏感度
    /// 计算: 阈值 = Math.Min(rect1.Height, rect2.Height)* VerticalThresholdRatio
    /// 调整建议:
    ///     0.1-0.3: 严格模式，只合并非常接近的行
    ///     0.5-0.8: 标准模式，合并段落内的行
    ///     1.0-3.0: 宽松模式，合并距离较远的文本块
    ///     >5.0: 几乎合并所有垂直分布的文本
    /// </summary>
    [ObservableProperty] public partial double VerticalThresholdRatio { get; set; } = 0.8;

    /// <summary>
    /// 水平相邻检测阈值比例
    /// 作用: 控制左右相邻文本块的合并敏感度
    /// 计算: 阈值 = Math.Min(rect1.Width, rect2.Width)* HorizontalThresholdRatio
    /// 调整建议:
    ///     0.1-0.3: 严格模式，只合并非常接近的词
    ///     0.5-0.8: 标准模式，合并同行内的词组
    ///     1.0-2.0: 宽松模式，合并分栏文本
    ///     >5.0: 几乎合并所有水平分布的文本
    /// </summary>
    [ObservableProperty] public partial double HorizontalThresholdRatio { get; set; } = 0.5;

    /// <summary>
    /// 换行检测阈值比例
    /// 作用: 控制合并文本时是否添加换行符
    /// 计算: 阈值 = lastRect.Height* LineSpacingThresholdRatio
    /// 调整建议:
    ///     0.1-0.2: 严格换行，即使很小的距离也换行
    ///     0.3-0.5: 标准换行，适合大多数文档
    ///     0.8-1.0: 宽松换行，只有距离很大才换行
    ///     >2.0: 几乎不换行，所有文本连在一起
    [ObservableProperty] public partial double LineSpacingThresholdRatio { get; set; } = 0.3;

    /// <summary>
    /// 词间距检测阈值比例
    /// 作用: 控制合并文本时是否添加空格
    /// 计算: 阈值 = lastRect.Width* WordSpacingThresholdRatio
    /// 调整建议:
    ///     0.1-0.2: 标准空格间距
    ///     0.3-0.5: 宽松空格间距
    ///     >1.0: 几乎不添加空格
    /// </summary>
    [ObservableProperty] public partial double WordSpacingThresholdRatio { get; set; } = 0.2;
    #endregion

    #region OCR Settings

    [ObservableProperty] public partial LangEnum OcrLanguage { get; set; } = LangEnum.Auto;
    [ObservableProperty] public partial bool IsOcrShowingAnnotated { get; set; } = false;
    [ObservableProperty] public partial bool IsOcrShowingTextControl { get; set; } = false;
    [ObservableProperty] public partial double OcrWindowWidth { get; set; } = 600;
    [ObservableProperty] public partial double OcrWindowHeight { get; set; } = 600;

    #endregion

    #region Image Translate Settings

    [ObservableProperty] public partial bool IsImTranShowingAnnotated { get; set; } = false;
    [ObservableProperty] public partial bool IsImTranShowingTextControl { get; set; } = false;
    [ObservableProperty] public partial double ImTranWindowWidth { get; set; } = 600;
    [ObservableProperty] public partial double ImTranWindowHeight { get; set; } = 600;

    #endregion

    [ObservableProperty] public partial LogEventLevel LogLevel { get; set; } = LogEventLevel.Information;

    [ObservableProperty] public partial ProxySettings Proxy { get; set; } = new();

    #endregion

    public void SetStorage(AppStorage<Settings> storage)
    {
        Storage = storage;

        // 属性更改时自动保存设置
        PropertyChanged += (s, e) =>
        {
            HandlePropertyChanged(e.PropertyName);

            if (e.PropertyName == nameof(MainWindowTop) ||
                e.PropertyName == nameof(MainWindowLeft) ||
                e.PropertyName == nameof(MainWindowWidth))
                SaveWithDebounce();
            else
                Save();
        };

        Proxy.PropertyChanged += (s, e) =>
        {
            Save();
        };
    }

    internal void Save() => Storage?.Save();

    private Timer? _saveTimer;
    private readonly Lock _timerLock = new();
    private const int DebounceTimeMs = 500; // 防抖时间
    internal void SaveWithDebounce()
    {
        lock (_timerLock)
        {
            // 如果计时器已存在，则重置
            _saveTimer?.Change(DebounceTimeMs, Timeout.Infinite);

            // 如果计时器不存在，则创建
            _saveTimer ??= new Timer(
                (state) =>
                {
                    // 计时器到点，执行真正的保存
                    Save();

                    // 释放计时器
                    lock (_timerLock)
                    {
                        _saveTimer?.Dispose();
                        _saveTimer = null;
                    }
                },
                null,
                DebounceTimeMs, // 500毫秒后执行
                Timeout.Infinite); // 只执行一次
        }
    }

    public void Initialize()
    {
        if (Storage is null)
        {
            throw new InvalidOperationException("Storage is not set. Please call SetStorage() before Initialize().");
        }
        ApplyLogLevel();
        ApplyStartup();
        ApplyStartMode();
    }

    public void LazyInitialize()
    {
        ApplyAppFont(true);
        ApplyLanguage(true);
        ApplyAppFontSize();
        ApplyTheme();
        ApplyDeactived();
    }

    private void ApplyStartup()
    {
        if (string.IsNullOrEmpty(DataLocation.StartupPath))
        {
            AutoStartup = false;
            return;
        }

        if (AutoStartup)
        {
            if (!Utilities.IsStartup())
                Utilities.SetStartup();
        }
        else
        {
            Utilities.UnSetStartup();
        }
    }

    private void ApplyStartMode()
    {
        if (StartMode == StartMode.SkipUACAdmin)
        {
            UACHelper.Create();
        }
        else
        {
#if !DEBUG
            if (UACHelper.Exist())
            {
                UACHelper.Delete();
            } 
#endif
        }
    }

    private void ApplyLanguage(bool initialize = false)
    {
        var i18n = Ioc.Default.GetRequiredService<Internationalization>();
        if (initialize)
            i18n.InitializeLanguage(Language);
        else
            i18n.ChangeLanguage(Language);
    }

    private void ApplyAppFont(bool initialize = false)
    {
        // 初始化时检查字体有效性
        if (initialize && !Fonts.SystemFontFamilies.Select(x => x.Source).Contains(AppFont))
        {
            AppFont = Win32Helper.GetSystemDefaultFont();
            return;
        }

        App.Current.Resources["ContentControlThemeFontFamily"] = new FontFamily(AppFont);

        // TODO: https://github.com/iNKORE-NET/UI.WPF.Modern/blob/main/source/iNKORE.UI.WPF.Modern/Themes/Controls/TextStyles.xaml#L13
        // 这句写的有问题  更新后避免更新系统字体键以避免带来其他问题
        App.Current.Resources[System.Windows.SystemFonts.MessageFontFamilyKey] = new FontFamily(AppFont);
    }

    private void ApplyAppFontSize()
    {
        App.Current.Resources["ControlContentThemeFontSize"] = TextFontSize;    //14
        App.Current.Resources["CaptionTextBlockFontSize"] = TextFontSize - 2;   //12
        App.Current.Resources["SubtitleTextBlockFontSize"] = TextFontSize + 6;  //18
    }

    private void ApplyTheme()
    {
        ThemeManager.SetRequestedTheme(App.Current.MainWindow, ColorScheme);
        var window = App.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (window != null)
        {
            ThemeManager.SetRequestedTheme(window, ColorScheme);
        }
        var promptWindow = App.Current.Windows.OfType<PromptEditWindow>().FirstOrDefault();
        if (promptWindow != null)
        {
            ThemeManager.SetRequestedTheme(promptWindow, ColorScheme);
        }
        var ocrWindow = App.Current.Windows.OfType<OcrWindow>().FirstOrDefault();
        if (ocrWindow != null)
        {
            ThemeManager.SetRequestedTheme(ocrWindow, ColorScheme);
        }
    }

    private void ApplyDeactived()
    {
        if (HideWhenDeactivated)
        {
            Win32Helper.HideFromAltTab(App.Current.MainWindow);
        }
        else
        {
            Win32Helper.ShowInAltTab(App.Current.MainWindow);
        }
    }

    private void ApplyLogLevel()
    {
        var loggingLevelSwitch = Ioc.Default.GetRequiredService<LoggingLevelSwitch>();
        loggingLevelSwitch.MinimumLevel = LogLevel;
    }

    private void HandlePropertyChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(AutoStartup):
                ApplyStartup();
                break;
            case nameof(StartMode):
                ApplyStartMode();
                break;
            case nameof(Language):
                ApplyLanguage();
                break;
            case nameof(AppFont):
                ApplyAppFont();
                break;
            case nameof(TextFontSize):
                ApplyAppFontSize();
                break;
            case nameof(ColorScheme):
                ApplyTheme();
                break;
            case nameof(HideWhenDeactivated):
                ApplyDeactived();
                break;
            case nameof(LogLevel):
                ApplyLogLevel();
                break;
            case nameof(DisableGlobalHotkeys):
                Ioc.Default.GetRequiredService<HotkeySettings>().ApplyGlobalHotkeys();
                break;
            case nameof(IgnoreHotkeysOnFullscreen):
                Ioc.Default.GetRequiredService<HotkeySettings>().ApplyIgnoreOnFullScreen();
                break;
            default:
                break;
        }
    }
}

#region Enumeration definition

public enum StartMode
{
    Normal,
    Admin,
    SkipUACAdmin
}

public enum LanguageDetectorType
{
    Local,
    Baidu,
    Tencent,
    Niutrans,
    Bing,
    Yandex,
    Google,
    Microsoft,
}

public enum LineBreakHandleType
{
    None,
    RemoveExtraLineBreak,
    RemoveAllLineBreak,
    RemoveAllLineBreakWithoutSpace,
}

public enum LayoutAnalysisMode
{
    MergeAll,
    StrictParagraph,
    StandardDocument,
    ColumnDocument,
    NoMerge,
    UserDefined,
}

public enum WindowScreenType
{
    RememberLastLaunchLocation,
    Cursor,
    Focus,
    Primary,
    Custom
}

public enum WindowAlignType
{
    Center,
    CenterTop,
    LeftTop,
    RightTop,
    Custom
}

#endregion