using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Web;
using WebDav;

namespace STranslate.ViewModels.Pages;

public partial class AboutViewModel(
    Settings settings,
    DataProvider dataProvider,
    ISnackbar snackbar,
    INotification notification,
    Internationalization i18n,
    ILogger<AboutViewModel> logger) : ObservableObject
{
    public Settings Settings { get; } = settings;
    public DataProvider DataProvider { get; } = dataProvider;
    [ObservableProperty] public partial string AppVersion { get; set; } = VersionInfo.GetVersion();

    #region ICommand

    [RelayCommand]
    private void LocateUserData()
    {
        var settingsFolderPath = Path.Combine(DataLocation.SettingsDirectory);
        var parentFolderPath = Path.GetDirectoryName(settingsFolderPath);
        if (Directory.Exists(parentFolderPath))
        {
            Process.Start("explorer.exe", parentFolderPath);
        }
    }

    [RelayCommand]
    private void LocateLog()
    {
        var logFolderPath = Path.Combine(Constant.LogDirectory);
        if (Directory.Exists(logFolderPath))
        {
            Process.Start("explorer.exe", logFolderPath);
        }
    }

    [RelayCommand]
    private void LocateSettings()
    {
        var settingsFolderPath = Path.Combine(DataLocation.SettingsDirectory);
        if (Directory.Exists(settingsFolderPath))
        {
            Process.Start("explorer.exe", settingsFolderPath);
        }
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (Settings.Backup.Type == BackupType.Local)
            await LocalBackupAsync();
        else
            await PreWebDavBackupAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (Settings.Backup.Type == BackupType.Local)
            await LocalRestoreAsync();
        else
            await WebDavRestoreAsync();
    }

    #endregion

    #region Local Backup

    private async Task LocalBackupAsync()
    {
        var saveFileDialog = new SaveFileDialog
        {
            Title = i18n.GetTranslation("SelectBackupFile"),
            Filter = "zip(*.zip)|*.zip",
            FileName = $"stranslate_backup_{DateTime.Now:yyyyMMddHHmmss}"
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        var dialog = new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation("ConfirmRestartAndBackup"),
        };
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var filePath = saveFileDialog.FileName;
        string[] args = [
            "backup",
            "-m", "backup",
            "-a", filePath,
            "-f", DataLocation.PluginsDirectory,
            "-f", DataLocation.SettingsDirectory,
            "-d", "3",
            "-l", DataLocation.AppExePath,
            "-c", DataLocation.InfoFilePath,
            "-w", string.Format(i18n.GetTranslation("BackupConfigSuccess"), filePath)
            ];
        Utilities.ExecuteProgram(DataLocation.HostExePath, args);
        App.Current.Shutdown();
    }

    private async Task LocalRestoreAsync()
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = i18n.GetTranslation("SelectRestoreFile"),
            Filter = "zip(*.zip)|*.zip"
        };
        if (openFileDialog.ShowDialog() != true)
            return;

        var dialog = new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation("ConfirmRestartAndRestore"),
        };
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var filePath = openFileDialog.FileName;
        string[] args = [
            "backup",
            "-m", "restore",
            "-a", filePath,
            "-s", Constant.Plugins,
            "-t", DataLocation.PluginsDirectory,
            "-s", Constant.Settings,
            "-t", DataLocation.SettingsDirectory,
            "-d", "3",
            "-l", DataLocation.AppExePath,
            "-c", DataLocation.InfoFilePath,
            "-w", string.Format(i18n.GetTranslation("RestoreConfigSuccess"), filePath)
            ];
        Utilities.ExecuteProgram(DataLocation.HostExePath, args);
        App.Current.Shutdown();
    }

    #endregion

    #region WebDav Backup

    private async Task PreWebDavBackupAsync()
    {
        // 测试连接是否成功
        var (isSucess, client, message) = await CreateClientAsync();
        if (!isSucess)
        {
            snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError($"Backup|CreateClientAsync|Failed Message: {message}");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation("ConfirmRestartAndBackup"),
        };
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var fileName = $"stranslate_backup_{DateTime.Now:yyyyMMddHHmmss}.zip";
        var filePath = Path.Combine(Constant.ProgramDirectory, fileName);
        string[] args = [
            "backup",
            "-m", "backup",
            "-a", filePath,
            "-f", DataLocation.PluginsDirectory,
            "-f", DataLocation.SettingsDirectory,
            "-d", "3",
            "-l", DataLocation.AppExePath,
            "-c", DataLocation.BackupFilePath,
            "-w", filePath
            ];
        Utilities.ExecuteProgram(DataLocation.HostExePath, args);
        App.Current.Shutdown();
    }

    public async Task PostWebDavBackupAsync(string filePath)
    {
        var (isSucess, client, message) = await CreateClientAsync();
        if (!isSucess)
        {
            notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError($"Backup|CreateClientAsync|Failed Message: {message}");
            return;
        }

        var fileName = Path.GetFileName(filePath);

        try
        {
            // 检查该路径是否存在
            var ret = await client.Propfind(message);
            if (!ret.IsSuccessful)
                // 不存在则创建目录
                await client.Mkcol(message);

            var fullPath = $"{message}{fileName}";
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var response = await client.PutFile(fullPath, fileStream);

            // 打印通知
            if (response.IsSuccessful && response.StatusCode == 201)
                notification.Show(i18n.GetTranslation("Prompt"), string.Format(i18n.GetTranslation("BackupSuccess"), fullPath));
            else
            {
                notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("BackupFailed"));
                logger.LogError($"Backup|PutFile|Error Code: {response.StatusCode} Description: {response.Description}");
            }
        }
        finally
        {
            try
            {
                File.Delete(filePath);
            }
            catch { }
        }
    }

    private async Task WebDavRestoreAsync()
    {
        var (isSucess, client, message) = await CreateClientAsync();
        if (!isSucess)
        {
            snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError($"Restore|CreateClientAsync|Failed Message: {message}");
            return;
        }

        try
        {
            //检查该路径是否存在
            var ret = await client.Propfind(message);
            if (!ret.IsSuccessful)
                //不存在则创建目录
                await client.Mkcol(message);
            else
                //添加结果到viewmodel
                foreach (var res in ret.Resources)
                {
                    if (res.IsCollection || res.Uri == null || !res.Uri.Contains(Constant.AppName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    //html解码以显示中文
                    var decodeFullName = HttpUtility.UrlDecode(res.Uri).Replace(message, "").Trim('/');
                    _collections.Add(new WebDavResult(decodeFullName));
                }

            var contentDialog = new WebDavContentDialog(client, message, _collections);
            if (await contentDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                CleanupCache(contentDialog.FilePath);
                return;
            }
            var dialog = new ContentDialog
            {
                Title = i18n.GetTranslation("Prompt"),
                PrimaryButtonText = i18n.GetTranslation("Confirm"),
                CloseButtonText = i18n.GetTranslation("Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = i18n.GetTranslation("ConfirmRestartAndRestore"),
            };
            var dialogResult = await dialog.ShowAsync();
            if (dialogResult != ContentDialogResult.Primary)
            {
                CleanupCache(contentDialog.FilePath);
                return;
            }

            string[] args = [
                "backup",
                "-m", "restore",
                "-a", contentDialog.FilePath,
                "-s", Constant.Plugins,
                "-t", DataLocation.PluginsDirectory,
                "-s", Constant.Settings,
                "-t", DataLocation.SettingsDirectory,
                "-r", contentDialog.FilePath,
                "-d", "3",
                "-l", DataLocation.AppExePath,
                "-c", DataLocation.InfoFilePath,
                "-w", i18n.GetTranslation("RestoreConfigSuccess")
            ];
            Utilities.ExecuteProgram(DataLocation.HostExePath, args);
            App.Current.Shutdown();
        }
        catch { }
        finally
        {
            _collections.Clear();
        }

        void CleanupCache(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    private ObservableCollection<WebDavResult> _collections = [];

    /// <summary>
    ///     创建WebDavClient
    /// </summary>
    /// <returns></returns>
    private async Task<(bool isSucess, WebDavClient client, string message)> CreateClientAsync()
    {
        // 如果没有/结尾则添加
        // * TeraCloud强制需要以/结尾
        // * 群晖、坚果云有无均可
        var uri = new Uri(Settings.Backup.Address.EndsWith('/') ? Settings.Backup.Address : $"{Settings.Backup.Address}/");

        // TeraCloud强制需要以/结尾
        // * 群晖、坚果云有无均可
        var absolutePath = $"{uri.LocalPath.TrimEnd('/')}/{Constant.AppName}/";

        var clientParams = new WebDavClientParams
        {
            Timeout = TimeSpan.FromSeconds(10),
            BaseAddress = uri,
            Credentials = new NetworkCredential(Settings.Backup.Username, Settings.Backup.Password)
        };
        var client = new WebDavClient(clientParams);
        try
        {
            var linkTest = await client.Propfind(string.Empty);
            var result = linkTest.IsSuccessful;
            return (result,
                result ? client : new WebDavClient(),
                result ? absolutePath : $"Code: {linkTest.StatusCode} Description: {linkTest.Description}");
        }
        catch (Exception ex)
        {
            return (false, new WebDavClient(), ex.Message);
        }
    }

    #endregion
}

public partial class WebDavResult : ObservableObject
{
    [ObservableProperty]
    public partial string FullName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEdit { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    public WebDavResult() { }

    public WebDavResult(string fullName, bool isEdit = false)
    {
        FullName = fullName;
        Name = FullName.Replace(".zip", "");
        IsEdit = isEdit;
    }
}