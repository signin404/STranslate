using CommunityToolkit.Mvvm.ComponentModel;
using iNKORE.UI.WPF.Modern.Controls;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace STranslate.Services;

public abstract partial class BaseService : ObservableObject, IDisposable
{
    protected abstract ServiceType ServiceType { get; }

    private readonly PluginManager _pluginManager;
    private readonly ServiceManager _serviceManager;
    private readonly PluginService _PluginService;
    private readonly ServiceSettings _serviceSettings;
    private readonly Internationalization _i18n;
    private bool _isInternalTrigger;

    protected List<ServiceData> SvcSettingDatas;
    protected Action? OnSvcPropertyChanged;

    [ObservableProperty] public partial ObservableCollection<PluginMetaData> Plugins { get; set; } = [];
    [ObservableProperty] public partial ObservableCollection<Service> Services { get; set; } = [];

    public BaseService(
        PluginManager pluginManager,
        ServiceManager serviceManager,
        PluginService PluginService,
        ServiceSettings serviceSettings,
        Internationalization i18n
        )
    {
        _pluginManager = pluginManager;
        _serviceManager = serviceManager;
        _PluginService = PluginService;
        _serviceSettings = serviceSettings;
        _i18n = i18n;

        SvcSettingDatas = ServiceType switch
        {
            ServiceType.Translation => _serviceSettings.TranSvcDatas,
            ServiceType.OCR => _serviceSettings.OcrSvcDatas,
            ServiceType.TTS => _serviceSettings.TtsSvcDatas,
            ServiceType.Vocabulary => _serviceSettings.VocabularySvcDatas,
            _ => _serviceSettings.TranSvcDatas,
        };

        _PluginService.PluginMetaDatas.CollectionChanged += OnPluginMetaDatasCollectionChanged;
        Services.CollectionChanged += OnServicesCollectionChanged;
    }

    public virtual T? GetActiveSvc<T>() where T : class, IPlugin => Services.FirstOrDefault(s => s.IsEnabled)?.Plugin as T;

    public async Task<Service?> AddAsync()
    {
        var title = ServiceType switch
        {
            ServiceType.Translation => "添加翻译服务",
            ServiceType.OCR => "添加文本识别服务",
            ServiceType.TTS => "添加语音合成服务",
            ServiceType.Vocabulary => "添加生词本服务",
            _ => "添加服务"
        };
        var contentDialog = new ServiceContentDialog(title, Plugins);
        if (await contentDialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (contentDialog.Result is PluginMetaData metaData)
            {
                var service = _serviceManager.AddService(metaData, ServiceType);
                // 非翻译服务默认关闭
                if (ServiceType != ServiceType.Translation)
                    service.IsEnabled = false;
                Services.Add(service);
                return service;
            }
        }

        return default;
    }

    public virtual async Task<bool> DeleteAsync(Service service)
    {
        var dialog = new ContentDialog
        {
            Title = _i18n.GetTranslation("ConfirmDelete"),
            PrimaryButtonText = _i18n.GetTranslation("Delete"),
            CloseButtonText = _i18n.GetTranslation("Close"),
            DefaultButton = ContentDialogButton.Close,
            Content = string.Format(_i18n.GetTranslation("ConfirmDeleteService"), service.DisplayName),
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _serviceManager.RemoveService(service, ServiceType);
            Services.Remove(service);

            return true;
        }

        return false;
    }

    public Service? Duplicate(Service service)
    {
        var result = _serviceManager.AddService(service.MetaData, ServiceType);
        result.DisplayName = service.DisplayName + "_New";
        Services.Insert(Services.IndexOf(service) + 1, result);
        return result;
    }

    protected virtual void LoadPlugins<T>() where T : IPlugin
    {
        _pluginManager.GetPluginMetaDatas<T>()
            .ToList()
            .ForEach(Plugins.Add);
    }

    protected virtual void LoadServices<T>() where T : IPlugin =>
        _serviceManager.AllServices
            .Where(p => p.Plugin is T)
            .OrderBy(s => GetServiceOrder(s.ServiceID))
            .ToList()
            .ForEach(Services.Add);

    protected virtual void LoadServices<T1, T2>() where T1 : IPlugin where T2 : IPlugin =>
        _serviceManager.AllServices
            .Where(p => p.Plugin is T1 or T2)
            .OrderBy(s => GetServiceOrder(s.ServiceID))
            .ToList()
            .ForEach(Services.Add);

    private int GetServiceOrder(string serviceId)
    {
        var index = SvcSettingDatas.FindIndex(d => d.SvcID == serviceId);
        return index == -1 ? int.MaxValue : index;
    }

    private void OnPluginMetaDatasCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null)
            return;

        foreach (PluginMetaData metaData in e.NewItems)
        {
            if (metaData.PluginType == null) continue;
            switch (ServiceType)
            {
                case ServiceType.Translation:
                    if (typeof(ITranslatePlugin).IsAssignableFrom(metaData.PluginType) ||
                        typeof(IDictionaryPlugin).IsAssignableFrom(metaData.PluginType))
                        Plugins.Add(metaData);
                    break;
                case ServiceType.OCR:
                    if (typeof(IOcrPlugin).IsAssignableFrom(metaData.PluginType))
                        Plugins.Add(metaData);
                    break;
                case ServiceType.TTS:
                    if (typeof(ITtsPlugin).IsAssignableFrom(metaData.PluginType))
                        Plugins.Add(metaData);
                    break;
                case ServiceType.Vocabulary:
                    if (typeof(IVocabularyPlugin).IsAssignableFrom(metaData.PluginType))
                        Plugins.Add(metaData);
                    break;
                default:
                    break;
            }
        }
    }

    private void OnServicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            // 更新服务顺序
            var oldIndex = e.OldStartingIndex;
            var newIndex = e.NewStartingIndex;
            if (oldIndex != newIndex && oldIndex >= 0 && newIndex >= 0 && oldIndex < Services.Count && newIndex < Services.Count)
            {
                var svc = Services[newIndex];
                var svcSetting = SvcSettingDatas.FirstOrDefault(s => s.SvcID == svc.ServiceID);
                if (svcSetting != null)
                {
                    SvcSettingDatas.Remove(svcSetting);
                    SvcSettingDatas.Insert(newIndex, svcSetting);
                    _serviceSettings.Save();
                }
            }
            return;
        }
        if (e.NewItems != null)
        {
            foreach (Service service in e.NewItems)
            {
                service.PropertyChanged += OnServicePropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (Service service in e.OldItems)
            {
                service.PropertyChanged -= OnServicePropertyChanged;
            }
        }
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Service svc)
            return;

        OnSvcPropertyChanged?.Invoke();

        var svcSetting = SvcSettingDatas.FirstOrDefault(s => s.SvcID == svc.ServiceID);
        if (svcSetting == null)
            return;

        switch (e.PropertyName)
        {
            case nameof(Service.IsEnabled):
                if (ServiceType == ServiceType.Translation)
                {
                    svcSetting.IsEnabled = svc.IsEnabled;
                    _serviceSettings.Save();
                }
                else
                {
                    HandleServiceEnabledChanged(svc, svcSetting);
                }
                break;
            case nameof(Service.DisplayName):
                svcSetting.Name = svc.DisplayName;
                _serviceSettings.Save();
                break;
            case nameof(TranslationOptions.AutoBackTranslation):
                if (svcSetting.Options != null && svc.Options != null)
                {
                    svcSetting.Options.AutoBackTranslation = svc.Options.AutoBackTranslation;
                    _serviceSettings.Save();
                }
                break;
            case nameof(TranslationOptions.ExecMode):
                if (svcSetting.Options != null && svc.Options != null)
                {
                    svcSetting.Options.ExecMode = svc.Options.ExecMode;
                    _serviceSettings.Save();
                }
                break;
        }
    }

    private void HandleServiceEnabledChanged(Service service, ServiceData svcSetting)
    {
        if (!service.IsEnabled)
        {
            svcSetting.IsEnabled = false;
            if (_isInternalTrigger) return;
            _serviceSettings.Save();
            return;
        }

        // 批量处理：确保只有一个服务启用
        EnsureSingleServiceEnabled(service, svcSetting);
    }

    private void EnsureSingleServiceEnabled(Service enabledService, ServiceData enabledSvcSetting)
    {
        try
        {
            _isInternalTrigger = true;

            // 批量禁用其他服务的UI状态
            foreach (var otherService in Services.Where(s => s != enabledService && s.IsEnabled))
            {
                otherService.IsEnabled = false;
            }

            // 批量更新设置数据
            foreach (var svcData in SvcSettingDatas.Where(x => x.IsEnabled && x.SvcID != enabledService.ServiceID))
            {
                svcData.IsEnabled = false;
            }

            // 设置当前服务为启用状态
            enabledSvcSetting.IsEnabled = true;

            // 一次性保存所有更改
            _serviceSettings.Save();
        }
        finally
        {
            _isInternalTrigger = false;
        }
    }

    public virtual void Dispose()
    {
        _PluginService.PluginMetaDatas.CollectionChanged -= OnPluginMetaDatasCollectionChanged;
        Services.CollectionChanged -= OnServicesCollectionChanged;
    }
}