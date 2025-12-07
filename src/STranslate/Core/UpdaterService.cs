using Microsoft.Extensions.Logging;
using STranslate.Plugin;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using Velopack;
using Velopack.Sources;
using MessageBox = iNKORE.UI.WPF.Modern.Controls.MessageBox;

namespace STranslate.Core;

public class UpdaterService(
    ILogger<UpdaterService> logger,
    Internationalization i18n,
    INotification notification
    )
{
    private SemaphoreSlim UpdateLock { get; } = new SemaphoreSlim(1);

    public async Task UpdateAppAsync(bool silentUpdate = true)
    {
        await UpdateLock.WaitAsync();
        try
        {
            if (!silentUpdate)
                notification.Show(i18n.GetTranslation("UpdateCheck"), i18n.GetTranslation("CheckingForUpdates"));

            var updateManager = new UpdateManager(new GithubSource(Constant.GitHub, accessToken: default, prerelease: false));

            var newUpdateInfo = await updateManager.CheckForUpdatesAsync();

            if (newUpdateInfo == null)
            {
                if (!silentUpdate)
                    MessageBox.Show(i18n.GetTranslation("NoUpdateInfoFound"), Constant.AppName);
                logger.LogInformation("No update info found.");
                return;
            }

            var newReleaseVersion = SemanticVersioning.Version.Parse(newUpdateInfo.TargetFullRelease.Version.ToString());
            var currentVersion = SemanticVersioning.Version.Parse(Constant.Version);

            logger.LogInformation($"Future Release <{JsonSerializer.Serialize(newUpdateInfo.TargetFullRelease)}>");

            if (newReleaseVersion <= currentVersion)
            {
                if (!silentUpdate)
                    MessageBox.Show(i18n.GetTranslation("AlreadyLatestVersion"), Constant.AppName);
                logger.LogInformation("You are already on the latest version.");
                return;
            }

            if (!silentUpdate)
                notification.Show(i18n.GetTranslation("UpdateAvailable"), string.Format(i18n.GetTranslation("NewVersionFound"), newReleaseVersion));
            logger.LogInformation($"New version {newReleaseVersion} found. Updating...");

            await updateManager.DownloadUpdatesAsync(newUpdateInfo);

            if (DataLocation.PortableDataLocationInUse())
            {
                FilesFolders.CopyAll(DataLocation.PortableDataPath, DataLocation.TmpConfigDirectory, MessageBox.Show);

                if (!FilesFolders.VerifyBothFolderFilesEqual(DataLocation.PortableDataPath, DataLocation.TmpConfigDirectory, MessageBox.Show))
                    MessageBox.Show(string.Format(i18n.GetTranslation("PortableDataMoveError"),
                        DataLocation.PortableDataPath,
                        DataLocation.TmpConfigDirectory), Constant.AppName);
            }

            var newVersionTips = NewVersionTips(newReleaseVersion.ToString());

            if (!silentUpdate)
                notification.Show(i18n.GetTranslation("UpdateReady"), newVersionTips);
            logger.LogInformation($"Update success:{newVersionTips}");

            if (MessageBox.Show(newVersionTips, Constant.AppName, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                updateManager.WaitExitThenApplyUpdates(newUpdateInfo);
                Application.Current.Shutdown();
            }
        }
        catch (Exception e)
        {
            if (e is HttpRequestException or WebException or SocketException ||
                e.InnerException is TimeoutException)
                logger.LogError(e, $"Check your connection and proxy settings to api.github.com.");
            else
                logger.LogError(e, $"Error Occurred");

            if (!silentUpdate)
                notification.Show(i18n.GetTranslation("UpdateFailed"), i18n.GetTranslation("UpdateFailedMessage"));
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private string NewVersionTips(string version) =>
        string.Format(i18n.GetTranslation("NewVersionTips"), version);
}