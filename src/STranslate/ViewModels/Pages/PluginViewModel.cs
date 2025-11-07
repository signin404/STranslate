using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using STranslate.Core;
using STranslate.Instances;
using STranslate.Plugin;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace STranslate.ViewModels.Pages;

public partial class PluginViewModel : ObservableObject
{
    private readonly PluginInstance _pluginInstance;
    private readonly IInternationalization _i18n;

    public DataProvider DataProvider { get; }

    private readonly ISnackbar _snackbar;
    private readonly CollectionViewSource _pluginCollectionView;
    public ICollectionView PluginCollectionView => _pluginCollectionView.View;

    [ObservableProperty] public partial string FilterText { get; set; } = string.Empty;

    public PluginViewModel(
        PluginInstance pluginInstance,
        IInternationalization i18n,
        DataProvider dataProvider,
        ISnackbar snackbar
        )
    {
        _pluginInstance = pluginInstance;
        _i18n = i18n;
        DataProvider = dataProvider;
        _snackbar = snackbar;

        _pluginCollectionView = new()
        {
            Source = _pluginInstance.PluginMetaDatas
        };
        _pluginCollectionView.Filter += OnPluginFilter;
    }

    [ObservableProperty]
    public partial PluginType PluginType { get; set; } = PluginType.All;

    private void OnPluginFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not PluginMetaData plugin)
        {
            e.Accepted = false;
            return;
        }

        // 类型筛选
        var typeMatch = PluginType switch
        {
            PluginType.Translate => typeof(ITranslatePlugin).IsAssignableFrom(plugin.PluginType) || typeof(IDictionaryPlugin).IsAssignableFrom(plugin.PluginType),
            PluginType.Ocr => typeof(IOcrPlugin).IsAssignableFrom(plugin.PluginType),
            PluginType.Tts => typeof(ITtsPlugin).IsAssignableFrom(plugin.PluginType),
            PluginType.Vocabulary => typeof(IVocabularyPlugin).IsAssignableFrom(plugin.PluginType),
            _ => true,
        };

        // 文本筛选
        var textMatch = string.IsNullOrEmpty(FilterText)
            || plugin.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || plugin.Author.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || plugin.Description.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

        e.Accepted = typeMatch && textMatch;
    }

    partial void OnPluginTypeChanged(PluginType value) => _pluginCollectionView.View?.Refresh();

    partial void OnFilterTextChanged(string value) => _pluginCollectionView.View?.Refresh();

    [RelayCommand]
    private void AddPlugin()
    {
        // Open a file dialog to select a plugin zip file
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _i18n.GetTranslation("SelectPluginFile"),
            Filter = "Spkg File (*.spkg)|*.spkg",
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog() != true)
        {
            return; // User canceled the dialog
        }
        var spkgPluginFilePath = dialog.FileName;
        var errMsg = _pluginInstance.InstallPlugin(spkgPluginFilePath);
        if (!string.IsNullOrEmpty(errMsg))
        {
            _ = new ContentDialog
            {
                Title = _i18n.GetTranslation("PluginInstallFailed"),
                CloseButtonText = _i18n.GetTranslation("Ok"),
                DefaultButton = ContentDialogButton.Close,
                Content = errMsg
            }.ShowAsync().ConfigureAwait(false);
        }
        else
        {
            _snackbar.ShowSuccess(_i18n.GetTranslation("PluginInstallSuccess"));
        }
    }

    [RelayCommand]
    private async Task PluginSummaryAsync(Button button)
    {
        var helpDialog = new ContentDialog()
        {
            Owner = Window.GetWindow(button),
            Content = new StackPanel
            {
                Children =
                {
                    GetTextBlock("PluginTypeAll", _pluginInstance.PluginMetaDatas.Count.ToString(), new Thickness()),
                    GetTextBlock("PluginTypeTranslate", _pluginInstance.PluginMetaDatas.Where(x => typeof(ITranslatePlugin).IsAssignableFrom(x.PluginType) || typeof(IDictionaryPlugin).IsAssignableFrom(x.PluginType)).Count().ToString(), new Thickness(0, 24, 0, 10)),
                    GetTextBlock("PluginTypeOcr", _pluginInstance.PluginMetaDatas.Where(x => typeof(IOcrPlugin).IsAssignableFrom(x.PluginType)).Count().ToString(), new Thickness(0, 24, 0, 10)),
                    GetTextBlock("PluginTypeTts", _pluginInstance.PluginMetaDatas.Where(x => typeof(ITtsPlugin).IsAssignableFrom(x.PluginType)).Count().ToString(), new Thickness(0, 24, 0, 10)),
                    GetTextBlock("PluginTypeVocabulary", _pluginInstance.PluginMetaDatas.Where(x => typeof(IVocabularyPlugin).IsAssignableFrom(x.PluginType)).Count().ToString(), new Thickness(0, 24, 0, 0)),
                }
            },
            PrimaryButtonText = (string)Application.Current.Resources["Ok"],
            DefaultButton = ContentDialogButton.Primary,
            CornerRadius = new CornerRadius(8),
            Style = (Style)Application.Current.Resources["ContentDialog"]
        };

        await helpDialog.ShowAsync();

        TextBlock GetTextBlock(string resourceKey, string text, Thickness thickness) =>
            new()
            {
                Text = $"{(string)Application.Current.Resources[resourceKey]}: {text}",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = thickness
            };
    }

    [RelayCommand]
    private void OpenPluginDirectory(PluginMetaData plugin)
    {
        var directory = plugin.PluginDirectory;
        if (!string.IsNullOrEmpty(directory))
            Process.Start("explorer.exe", directory);
    }

    [RelayCommand]
    private async Task DeletePluginAsync(PluginMetaData plugin)
    {
        if (await new ContentDialog
        {
            Title = _i18n.GetTranslation("Prompt"),
            CloseButtonText = _i18n.GetTranslation("Cancel"),
            PrimaryButtonText = _i18n.GetTranslation("Confirm"),
            DefaultButton = ContentDialogButton.Primary,
            Content = string.Format(_i18n.GetTranslation("PluginDeleteConfirm"), plugin.Author, plugin.Version, plugin.Name),
        }.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!_pluginInstance.UninstallPlugin(plugin))
        {
            _ = new ContentDialog
            {
                Title = _i18n.GetTranslation("Prompt"),
                CloseButtonText = _i18n.GetTranslation("Ok"),
                DefaultButton = ContentDialogButton.Close,
                Content = _i18n.GetTranslation("PluginDeleteFailed")
            }.ShowAsync().ConfigureAwait(false);

            return;
        }

        if (await new ContentDialog
        {
            Title = _i18n.GetTranslation("Prompt"),
            CloseButtonText = _i18n.GetTranslation("Cancel"),
            PrimaryButtonText = _i18n.GetTranslation("Confirm"),
            DefaultButton = ContentDialogButton.Primary,
            Content = _i18n.GetTranslation("PluginDeleteForRestart"),
        }.ShowAsync() == ContentDialogResult.Primary)
        {
            //TODO: 重启程序
            App.Current.Shutdown();
        }
    }

    [RelayCommand]
    private void OpenOfficialLink(string url)
        => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
}

public enum PluginType
{
    All,
    Translate,
    Ocr,
    Tts,
    Vocabulary,
}