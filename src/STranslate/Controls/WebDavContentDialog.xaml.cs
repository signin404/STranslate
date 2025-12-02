using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.Logging;
using STranslate.Plugin;
using STranslate.ViewModels.Pages;
using System.Collections.ObjectModel;
using System.IO;
using WebDav;

namespace STranslate.Controls;

public partial class WebDavContentDialog
{
    private readonly ILogger<WebDavContentDialog> _logger = Ioc.Default.GetRequiredService<ILogger<WebDavContentDialog>>();
    private readonly ISnackbar _snackbar = Ioc.Default.GetRequiredService<ISnackbar>();
    private readonly WebDavClient _webDavClient;
    private readonly string _absolutePath;

    public ObservableCollection<WebDavResult> Collections { get; }

    public string FilePath { get; private set; } = string.Empty;

    public WebDavContentDialog(
        WebDavClient webDavClient,
        string absolutePath,
        ObservableCollection<WebDavResult> collections)
    {
        InitializeComponent();

        DataContext = this;

        _webDavClient = webDavClient;
        _absolutePath = absolutePath;
        Collections = collections;
    }

    private ContentDialogResult _result = ContentDialogResult.None;

    public new async Task<ContentDialogResult> ShowAsync()
    {
        await base.ShowAsync();
        return _result;
    }

    [RelayCommand]
    private async Task DownloadAsync(string fullName)
    {
        // 下载逻辑
        if (_absolutePath == null || _webDavClient == null)
            return;

        try
        {
            var path = $"{_absolutePath.TrimEnd('/')}/{fullName}";
            if (File.Exists(fullName))
            {
                File.Delete(fullName);
            }
            FilePath = fullName;

            using var response = await _webDavClient.GetRawFile(path);
            if (response.IsSuccessful && response.StatusCode == 200)
            {
                using var fileStream = File.Create(fullName);
                await response.Stream.CopyToAsync(fileStream);

                _snackbar.ShowSuccess("下载成功");

                _result = ContentDialogResult.Primary;
                Hide();
            }
            else
            {
                _snackbar.ShowError($"下载失败：{response.StatusCode} {response.Description}");
                _logger.LogError("WebDav 下载失败：{StatusCode} {Description}", response.StatusCode, response.Description);
            }
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"下载异常：{ex.Message}");
            _logger.LogError(ex, "WebDav 下载异常");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(string fullName)
    {
        if (_absolutePath == null || _webDavClient == null)
            return;

        var path = $"{_absolutePath.TrimEnd('/')}/{fullName}";
        var response = await _webDavClient.Delete(path);
        if (response.IsSuccessful && response.StatusCode == 204)
            Collections.Remove(Find(fullName));
        else
        {
            _snackbar.ShowError($"删除失败：{response.StatusCode} {response.Description}");
            _logger.LogError("WebDav 删除失败：{StatusCode} {Description}", response.StatusCode, response.Description);
        }
    }

    private WebDavResult Find(string fullName)
    {
        return Collections.First(x => x.FullName == fullName);
    }
}
