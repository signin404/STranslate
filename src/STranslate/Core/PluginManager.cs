using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using STranslate.Plugin;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace STranslate.Core;

public class PluginManager
{
    private readonly ILogger<PluginManager> _logger;
    private readonly List<PluginMetaData> _pluginMetaDatas;
    private readonly string _tempExtractPath;

    public PluginManager(ILogger<PluginManager> logger)
    {
        _logger = logger;
        _pluginMetaDatas = [];
        _tempExtractPath = Path.Combine(Path.GetTempPath(), Constant.TmpPluginFolderName);

        Directory.CreateDirectory(Constant.PreinstalledDirectory);
        Directory.CreateDirectory(DataLocation.PluginsDirectory);
        Directory.CreateDirectory(DataLocation.PluginCacheDirectory);
        Directory.CreateDirectory(_tempExtractPath);
    }

    /// <summary>
    /// 所有已加载的插件元数据
    /// </summary>
    public IEnumerable<PluginMetaData> AllPluginMetaDatas => _pluginMetaDatas;

    /// <summary>
    /// 获取指定类型的插件元数据
    /// </summary>
    /// <typeparam name="T">插件类型</typeparam>
    /// <returns>匹配的插件元数据</returns>
    public IEnumerable<PluginMetaData> GetPluginMetaDatas<T>() where T : IPlugin
        => _pluginMetaDatas.Where(d => d.PluginType != null && typeof(T).IsAssignableFrom(d.PluginType));

    public void LoadPlugins()
    {
        var results = LoadPluginMetaDatasFromDirectories(DataLocation.PluginDirectories);
        foreach (var result in results)
        {
            if (result.IsSuccess && result.PluginMetaData != null)
            {
                _pluginMetaDatas.Add(result.PluginMetaData);
            }
            else
            {
                _logger.LogError($"Failed to load plugin {result.PluginName}: {result.ErrorMessage}");
            }
        }
    }

    public PluginInstallResult InstallPlugin(string spkgFilePath)
    {
        if (!TryValidatePackagePath(spkgFilePath, out var validationError))
        {
            return PluginInstallResult.Fail(validationError);
        }

        var extractPath = GetExtractionPath(spkgFilePath);
        _logger.LogDebug($"Loading plugin from SPKG: {Path.GetFileNameWithoutExtension(spkgFilePath)}");

        try
        {
            if (!TryPrepareExtractionDirectory(extractPath, out var preparationError))
            {
                return PluginInstallResult.Fail(preparationError);
            }

            if (!TryExtractPackage(spkgFilePath, extractPath, out var extractionError))
            {
                TryCleanupExtractionDirectory(extractPath);
                return PluginInstallResult.Fail(extractionError);
            }

            PluginMetaData? metaData = null;
            try
            {
                metaData = GetPluginMeta(extractPath);
            }
            catch (Exception ex)
            {
                TryCleanupExtractionDirectory(extractPath);
                return PluginInstallResult.Fail($"Unexpected error reading plugin metadata: {ex.Message}");
            }

            if (metaData == null || string.IsNullOrWhiteSpace(metaData.PluginID))
            {
                TryCleanupExtractionDirectory(extractPath);
                var metaSnapshot = metaData == null ? "null" : JsonSerializer.Serialize(metaData);
                return PluginInstallResult.Fail("Invalid plugin structure: " + metaSnapshot);
            }

            var existingPlugin = FindInstalledPlugin(metaData.PluginID);
            if (existingPlugin != null)
            {
                var upgradeDecision = EvaluateExistingPlugin(existingPlugin, metaData);

                if (!upgradeDecision.RequiredUpgrade)
                {
                    if (!upgradeDecision.Succeeded)
                    {
                        TryCleanupExtractionDirectory(extractPath);
                    }

                    return upgradeDecision;
                }

                // Leave extraction directory in place for UpgradePlugin to consume.
                return upgradeDecision;
            }

            var loadResult = InstallExtractedPlugin(extractPath, metaData);
            if (!loadResult.IsSuccess || loadResult.PluginMetaData == null)
            {
                TryCleanupExtractionDirectory(extractPath);
                return PluginInstallResult.Fail("Failed to load plugin from " + extractPath + ": " + loadResult.ErrorMessage);
            }

            var installedPlugin = loadResult.PluginMetaData;
            _pluginMetaDatas.Add(installedPlugin);

            LoadPluginLanguageResources(installedPlugin.PluginDirectory);
            return PluginInstallResult.Success(installedPlugin);
        }
        catch (Exception ex)
        {
            TryCleanupExtractionDirectory(extractPath);
            _logger.LogError(ex, $"Unexpected error loading plugin from SPKG {spkgFilePath}.");
            return PluginInstallResult.Fail("Unexpected error loading plugin from SPKG " + spkgFilePath + ": " + ex.Message);
        }
    }

    public bool UpgradePlugin(PluginMetaData oldPlugin, string spkgFilePath)
    {
        try
        {
            var extractPath = GetExtractionPath(spkgFilePath);

            if (!Directory.Exists(extractPath))
            {
                _logger.LogError($"Upgrade failed because extraction directory was missing: {extractPath}");
                return false;
            }

            // 标记旧插件目录以便在重启时删除
            File.Create(Path.Combine(oldPlugin.PluginDirectory, Constant.NeedDelete)).Dispose();

            // 将新插件移动到目标位置
            var targetPath = oldPlugin.PluginDirectory + Constant.NeedUpgrade;
            Helper.MoveDirectory(extractPath, targetPath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to upgrade plugin {oldPlugin.Name}.");
            return false;
        }
    }

    public bool UninstallPlugin(PluginMetaData metaData)
    {
        // 标记插件目录删除
        File.Create(Path.Combine(metaData.PluginDirectory, Constant.NeedDelete)).Dispose();

        // 插件设置目录删除
        var combineName = Helper.GetPluginDicrtoryName(metaData);
        var pluginSettingDirectory = Path.Combine(DataLocation.PluginSettingsDirectory, combineName);
        if (Directory.Exists(pluginSettingDirectory))
            File.Create(Path.Combine(pluginSettingDirectory, Constant.NeedDelete)).Dispose();

        // 插件缓存目录删除
        var pluginCacheDirectory = Path.Combine(DataLocation.PluginCacheDirectory, combineName);
        if (Directory.Exists(pluginCacheDirectory))
            File.Create(Path.Combine(pluginCacheDirectory, Constant.NeedDelete)).Dispose();

        _pluginMetaDatas.Remove(metaData);

        return true;
    }

    public void CleanupTempFiles()
    {
        try
        {
            if (Directory.Exists(_tempExtractPath))
            {
                Directory.Delete(_tempExtractPath, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to cleanup temp files: {ex.Message}");
        }
    }

    #region Private Methods

    private PluginLoadResult LoadPluginMetaDataFromDirectory(string pluginDirectory)
    {
        var metaData = GetPluginMeta(pluginDirectory);
        if (metaData == null)
        {
            return PluginLoadResult.Fail("Failed to load plugin metadata", Path.GetFileName(pluginDirectory));
        }

        var result = LoadPluginPairFromMetaData(metaData);

        // 记录加载结果
        if (result.IsSuccess)
        {
            _logger.LogInformation($"插件加载成功: {result.PluginMetaData?.Name}");
        }
        else
        {
            _logger.LogError($"插件加载失败: {result.PluginName} - {result.ErrorMessage}");
        }

        return result;
    }

    private List<PluginLoadResult> LoadPluginMetaDatasFromDirectories(params string[] pluginDirectories)
    {
        var allPluginMetaDatas = GetAllPluginMetaData(pluginDirectories);
        var (uniqueList, duplicateList) = GetUniqueLatestPluginMeta(allPluginMetaDatas);

        LogDuplicatePlugins(duplicateList);

        var results = new List<PluginLoadResult>();
        foreach (var metaData in uniqueList)
        {
            var result = LoadPluginPairFromMetaData(metaData);
            results.Add(result);
        }

        LogPluginLoadResults(results);

        return results;
    }

    private bool TryValidatePackagePath(string spkgFilePath, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(spkgFilePath))
        {
            errorMessage = "Plugin path cannot be null or empty.";
            return false;
        }

        if (!File.Exists(spkgFilePath))
        {
            errorMessage = "Plugin file does not exist: " + spkgFilePath;
            return false;
        }

        var extension = Path.GetExtension(spkgFilePath).ToLowerInvariant();
        if (extension != Constant.PluginFileExtension)
        {
            errorMessage = "Unsupported plugin file type: " + extension + ". Expected .spkg";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private string GetExtractionPath(string spkgFilePath)
    {
        var pluginName = Path.GetFileNameWithoutExtension(spkgFilePath);
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            throw new InvalidOperationException("Unable to determine plugin name from package path.");
        }

        return Path.Combine(_tempExtractPath, pluginName);
    }

    private bool TryPrepareExtractionDirectory(string extractPath, out string errorMessage)
    {
        try
        {
            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, true);
            }

            Directory.CreateDirectory(extractPath);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = "Failed to prepare extraction directory: " + ex.Message;
            return false;
        }
    }

    private bool TryExtractPackage(string spkgFilePath, string extractPath, out string errorMessage)
    {
        try
        {
            ZipFile.ExtractToDirectory(spkgFilePath, extractPath);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = "Failed to extract SPKG file: " + ex.Message;
            return false;
        }
    }

    private PluginInstallResult EvaluateExistingPlugin(PluginMetaData existingPlugin, PluginMetaData incomingPlugin)
    {
        if (!Version.TryParse(incomingPlugin.Version, out var incomingVersion))
        {
            return PluginInstallResult.Fail($"无法解析插件版本: {incomingPlugin.Version}", existingPlugin);
        }

        if (Version.TryParse(existingPlugin.Version, out var installedVersion) && incomingVersion <= installedVersion)
        {
            var message = $"插件版本过旧: {incomingPlugin.Name} v{incomingPlugin.Version}，当前已安装版本为 v{existingPlugin.Version}。";
            return PluginInstallResult.Fail(message, existingPlugin);
        }

        var upgradeMessage = $"可以选择升级到新版本(v{incomingPlugin.Version})。";
        return PluginInstallResult.RequiresUpgrade(upgradeMessage, existingPlugin);
    }

    private PluginLoadResult InstallExtractedPlugin(string extractPath, PluginMetaData metaData)
    {
        try
        {
            var pluginPath = MoveToPluginPath(extractPath, metaData.PluginID);
            return LoadPluginMetaDataFromDirectory(pluginPath);
        }
        catch (Exception ex)
        {
            return PluginLoadResult.Fail("Failed to finalize plugin installation: " + ex.Message, metaData.Name, ex);
        }
    }

    private void LoadPluginLanguageResources(string? pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return;
        }

        Ioc.Default.GetRequiredService<Internationalization>()
            .LoadInstalledPluginLanguages(pluginDirectory);
    }

    private void TryCleanupExtractionDirectory(string extractPath)
    {
        try
        {
            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to cleanup extraction directory {extractPath}: {ex.Message}");
        }
    }

    private PluginMetaData? FindInstalledPlugin(string pluginId)
        => _pluginMetaDatas.FirstOrDefault(x => x.PluginID == pluginId);

    private PluginLoadResult LoadPluginPairFromMetaData(PluginMetaData metaData)
    {
        try
        {
            var assemblyLoader = new PluginAssemblyLoader(metaData.ExecuteFilePath);
            var assembly = assemblyLoader.LoadAssemblyAndDependencies();

            if (assembly == null)
            {
                return PluginLoadResult.Fail("Assembly loading failed", metaData.Name);
            }

            var type = assemblyLoader.FromAssemblyGetTypeOfInterface(assembly, typeof(IPlugin));
            if (type == null)
            {
                return PluginLoadResult.Fail("IPlugin interface not found", metaData.Name);
            }

            var assemblyName = assembly.GetName().Name;
            if (assemblyName == null)
            {
                return PluginLoadResult.Fail("Assembly name is null", metaData.Name);
            }

            metaData.AssemblyName = assemblyName;
            metaData.PluginType = type;

            UpdateDirectories(metaData);

            return PluginLoadResult.Success(metaData);
        }
        catch (FileNotFoundException ex)
        {
            return PluginLoadResult.Fail($"Plugin file not found: {ex.FileName}", metaData.Name, ex);
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaderErrors = string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message));
            return PluginLoadResult.Fail($"Type loading failed: {loaderErrors}", metaData.Name, ex);
        }
        catch (Exception ex)
        {
            return PluginLoadResult.Fail($"Plugin loading error: {ex.Message}", metaData.Name, ex);
        }
    }

    private List<PluginMetaData> GetAllPluginMetaData(string[] pluginDirectories)
    {
        var allPluginMetaDatas = new List<PluginMetaData>();
        var directories = pluginDirectories.SelectMany(Directory.EnumerateDirectories);

        foreach (var directory in directories)
        {
            var tmp = directory;
            if (Helper.ShouldDeleteDirectory(tmp))
            {
                Helper.TryDeleteDirectory(tmp);
                continue;
            }

            if (tmp.EndsWith(Constant.NeedUpgrade))
            {
                var getOriginDirectory = tmp[..^Constant.NeedUpgrade.Length];
                Directory.Move(tmp, getOriginDirectory);
                tmp = getOriginDirectory;
            }

            var metadata = GetPluginMeta(tmp);
            if (metadata != null)
            {
                allPluginMetaDatas.Add(metadata);
            }
        }

        return allPluginMetaDatas;
    }

    private PluginMetaData? GetPluginMeta(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
        {
            return null;
        }

        string configPath = Path.Combine(pluginDirectory, Constant.PluginMetaFileName);
        if (!File.Exists(configPath))
        {
            _logger.LogWarning($"Plugin config file not found: {configPath}");
            return null;
        }

        try
        {
            var content = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning($"Plugin config file is empty: {configPath}");
                return null;
            }

            var metaData = JsonSerializer.Deserialize<PluginMetaData>(content);
            if (metaData == null)
            {
                _logger.LogWarning($"Failed to deserialize plugin metadata: {configPath}");
                return null;
            }

            metaData.PluginDirectory = pluginDirectory;

            if (!File.Exists(metaData.ExecuteFilePath))
            {
                _logger.LogWarning($"Plugin executable file not found: {metaData.ExecuteFilePath}");
                return null;
            }

            // 预装插件
            if (pluginDirectory.Contains(Constant.PreinstalledDirectory))
            {
                metaData.IsPrePlugin = true;
            }

            return metaData;
        }
        catch (JsonException ex)
        {
            _logger.LogError($"Invalid JSON in plugin config {configPath}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error reading plugin config {configPath}: {ex.Message}");
            return null;
        }
    }

    private void UpdateDirectories(PluginMetaData metaData)
    {
        var combineName = Helper.GetPluginDicrtoryName(metaData);
        // 插件服务数据加载路径
        metaData.PluginSettingsDirectoryPath = Path.Combine(DataLocation.PluginSettingsDirectory, combineName);
        // 插件自己确保目录存在
        metaData.PluginCacheDirectoryPath = Path.Combine(DataLocation.PluginCacheDirectory, combineName);
    }

    private (List<PluginMetaData> UniqueList, List<PluginMetaData> DuplicateList) GetUniqueLatestPluginMeta(List<PluginMetaData> allPluginMetaDatas)
    {
        var grouped = allPluginMetaDatas
            .GroupBy(x => x.PluginID)
            .ToList();

        var uniqueList = new List<PluginMetaData>();
        var duplicateList = new List<PluginMetaData>();

        foreach (var group in grouped)
        {
            if (group.Count() == 1)
            {
                uniqueList.Add(group.First());
                continue;
            }

            var sorted = group
                .OrderByDescending(GetVersionOrDefault)
                .ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();

            uniqueList.Add(sorted.First());
            duplicateList.AddRange(sorted.Skip(1));
        }

        return (uniqueList, duplicateList);
    }

    private static Version GetVersionOrDefault(PluginMetaData metaData)
    {
        if (Version.TryParse(metaData.Version, out var parsed))
        {
            return parsed;
        }

        return new Version(0, 0, 0);
    }

    private string MoveToPluginPath(string extractPath, string pluginID)
    {
        if (!Directory.Exists(extractPath))
        {
            throw new DirectoryNotFoundException($"Extract path does not exist: {extractPath}");
        }

        var pluginName = Path.GetFileName(extractPath);
        if (string.IsNullOrEmpty(pluginName) || string.IsNullOrWhiteSpace(pluginID))
        {
            throw new InvalidOperationException("Cannot determine plugin name or plugin id from extract path");
        }

        // 根据是否为预装插件决定目标路径
        var targetPath = Constant.PrePluginIDs.Contains(pluginID)
            ? Path.Combine(Constant.PreinstalledDirectory, pluginName)
            : Path.Combine(DataLocation.PluginsDirectory, $"{pluginName}_{pluginID}");

        try
        {
            Helper.MoveDirectory(extractPath, targetPath);
            return targetPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to move plugin to target path: {ex.Message}", ex);
        }
    }

    private void LogDuplicatePlugins(List<PluginMetaData> duplicateList)
    {
        if (duplicateList.Count == 0)
        {
            return;
        }

        _logger.LogWarning($"发现 {duplicateList.Count} 个重复插件，将跳过加载:");

        foreach (var duplicate in duplicateList)
        {
            var pluginType = duplicate.IsPrePlugin ? "预装插件" : "用户插件";
            var directoryInfo = !string.IsNullOrEmpty(duplicate.PluginDirectory)
                ? $" | 目录: {Path.GetFileName(duplicate.PluginDirectory)}"
                : "";
            var authorInfo = !string.IsNullOrEmpty(duplicate.Author)
                ? $" | 作者: {duplicate.Author}"
                : "";
            var websiteInfo = !string.IsNullOrEmpty(duplicate.Website)
                ? $" | 网站: {duplicate.Website}"
                : "";

            _logger.LogWarning($"  ↳ 跳过重复插件: {duplicate.Name} v{duplicate.Version} " +
                             $"(ID: {duplicate.PluginID}) | 类型: {pluginType}" +
                             $"{authorInfo}{directoryInfo}{websiteInfo}");
        }
    }

    private void LogPluginLoadResults(List<PluginLoadResult> results)
    {
        var successful = results.Count(r => r.IsSuccess);
        var failed = results.Count(r => !r.IsSuccess);
        var total = results.Count;

        _logger.LogInformation($"插件加载完成: 总计 {total} 个插件，成功 {successful} 个，失败 {failed} 个");

        // 记录成功加载的插件详情
        var successfulPlugins = results.Where(r => r.IsSuccess && r.PluginMetaData != null).ToList();
        if (successfulPlugins.Count > 0)
        {
            _logger.LogInformation($"成功加载的插件列表:");
            foreach (var success in successfulPlugins)
            {
                var metadata = success.PluginMetaData!;
                var pluginType = metadata.IsPrePlugin ? "预装插件" : "用户插件";
                var authorInfo = !string.IsNullOrEmpty(metadata.Author)
                    ? $" | 作者: {metadata.Author}"
                    : "";
                var assemblyInfo = !string.IsNullOrEmpty(metadata.AssemblyName)
                    ? $" | 程序集: {metadata.AssemblyName}"
                    : "";

                _logger.LogInformation($"  ✓ {metadata.Name} v{metadata.Version} " +
                                     $"(ID: {metadata.PluginID}) | 类型: {pluginType}" +
                                     $"{authorInfo}{assemblyInfo}");
            }
        }

        // 记录失败的插件详情
        var failedPlugins = results.Where(r => !r.IsSuccess).ToList();
        if (failedPlugins.Count > 0)
        {
            _logger.LogError($"加载失败的插件列表:");
            foreach (var failure in failedPlugins)
            {
                var pluginName = failure.PluginName ?? "未知插件";
                var errorMessage = failure.ErrorMessage ?? "未知错误";
                var exceptionInfo = failure.Exception != null
                    ? $" | 异常类型: {failure.Exception.GetType().Name}"
                    : "";

                _logger.LogError($"  ✗ {pluginName}: {errorMessage}{exceptionInfo}");

                // 如果有内部异常，也记录下来
                if (failure.Exception?.InnerException != null)
                {
                    _logger.LogError($"    ↳ 内部异常: {failure.Exception.InnerException.Message}");
                }
            }
        }
    }

    #endregion
}

public class PluginLoadResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }
    public PluginMetaData? PluginMetaData { get; set; }
    public string? PluginName { get; set; }

    public static PluginLoadResult Success(PluginMetaData metaData) => new()
    {
        IsSuccess = true,
        PluginMetaData = metaData,
        PluginName = metaData.Name
    };

    public static PluginLoadResult Fail(string message, string? pluginName = null, Exception? ex = null) => new()
    {
        IsSuccess = false,
        ErrorMessage = message,
        PluginName = pluginName,
        Exception = ex
    };
}

public enum PluginInstallStatus
{
    Success,
    Failure,
    UpgradeRequired
}

public sealed class PluginInstallResult
{
    private PluginInstallResult(PluginInstallStatus status, PluginMetaData? newPlugin, PluginMetaData? existingPlugin, string? message)
    {
        Status = status;
        NewPlugin = newPlugin;
        ExistingPlugin = existingPlugin;
        Message = message;
    }

    public PluginInstallStatus Status { get; }
    public PluginMetaData? NewPlugin { get; }
    public PluginMetaData? ExistingPlugin { get; }
    public string? Message { get; }

    public bool Succeeded => Status == PluginInstallStatus.Success;
    public bool RequiredUpgrade => Status == PluginInstallStatus.UpgradeRequired;

    public static PluginInstallResult Success(PluginMetaData plugin)
        => new(PluginInstallStatus.Success, plugin, null, null);

    public static PluginInstallResult Fail(string message, PluginMetaData? existing = null)
        => new(PluginInstallStatus.Failure, null, existing, message);

    public static PluginInstallResult RequiresUpgrade(string message, PluginMetaData existing)
        => new(PluginInstallStatus.UpgradeRequired, null, existing, message);
}