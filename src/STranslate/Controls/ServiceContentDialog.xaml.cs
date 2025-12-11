using CommunityToolkit.Mvvm.DependencyInjection;
using iNKORE.UI.WPF.Modern.Controls;
using STranslate.Core;
using STranslate.Plugin;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace STranslate.Controls;

public partial class ServiceContentDialog : INotifyPropertyChanged
{
    private readonly Internationalization _i18n = Ioc.Default.GetRequiredService<Internationalization>();

    public ServiceContentDialog(string title, ObservableCollection<PluginMetaData> itemsSource)
    {
        ServiceTitle = title;

        InitializeComponent();

        DataContext = this;

        _collectionViewSource = new() { Source = itemsSource };
        _collectionViewSource.Filter += OnFilter;

        // 使用自定义分组
        _collectionViewSource.GroupDescriptions.Add(new CustomPluginGroupDescription(_i18n));

        // 自定义排序
        if (_collectionViewSource.View is ListCollectionView listCollectionView)
        {
            listCollectionView.CustomSort = new PluginMetaDataComparer(_i18n);
        }
    }

    private void OnFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not PluginMetaData plugin)
        {
            e.Accepted = false;
            return;
        }

        // 文本筛选
        var textMatch = string.IsNullOrEmpty(FilterText) || plugin.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
        e.Accepted = textMatch;
    }

    private readonly CollectionViewSource _collectionViewSource;
    public ICollectionView CollectionView => _collectionViewSource.View;

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText != value)
            {
                _filterText = value;
                _collectionViewSource.View?.Refresh();
                OnPropertyChanged();
            }
        }
    }

    public string ServiceTitle
    {
        get => (string)GetValue(ServiceTitleProperty);
        set => SetValue(ServiceTitleProperty, value);
    }

    public static readonly DependencyProperty ServiceTitleProperty =
        DependencyProperty.Register(
            nameof(ServiceTitle),
            typeof(string),
            typeof(ServiceContentDialog),
            new PropertyMetadata(string.Empty));

    public object SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public object? Result { get; set; }

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(ServiceContentDialog), null);

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // 关闭前先缓存结果，以防数据源被释放
        Result = SelectedItem;
        _collectionViewSource.Filter -= OnFilter;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.F || Keyboard.Modifiers is not ModifierKeys.Control) return;

        PART_FilterTextBox.Focus();
        PART_FilterTextBox.SelectAll();
    }
}

internal class CustomPluginGroupDescription(Internationalization i18n) : GroupDescription
{
    public override object GroupNameFromItem(object item, int level, System.Globalization.CultureInfo culture)
    {
        if (item is not PluginMetaData plugin)
        {
            throw new ArgumentException("Item is not of type PluginMetaData", nameof(item));
        }

        var isBuiltIn = plugin.IsPrePlugin && plugin.Name.Contains(i18n.GetTranslation("BuiltIn"), StringComparison.OrdinalIgnoreCase);

        if (isBuiltIn)
        {
            return i18n.GetTranslation("BuiltIn");
        }

        if (plugin.IsPrePlugin)
        {
            return i18n.GetTranslation("PreInstall");
        }

        return i18n.GetTranslation("Extend");
    }
}

internal class PluginMetaDataComparer(Internationalization i18n) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not PluginMetaData pluginX || y is not PluginMetaData pluginY)
        {
            return 0;
        }

        var isBuiltInX = pluginX.IsPrePlugin && pluginX.Name.Contains(i18n.GetTranslation("BuiltIn"), StringComparison.OrdinalIgnoreCase);
        var isBuiltInY = pluginY.IsPrePlugin && pluginY.Name.Contains(i18n.GetTranslation("BuiltIn"), StringComparison.OrdinalIgnoreCase);

        // 优先级1: IsPrePlugin=true 且 Name 包含"内置"
        if (isBuiltInX && !isBuiltInY)
        {
            return -1;
        }

        if (!isBuiltInX && isBuiltInY)
        {
            return 1;
        }

        // 优先级2: IsPrePlugin=true
        if (pluginX.IsPrePlugin && !pluginY.IsPrePlugin)
        {
            return -1;
        }

        if (!pluginX.IsPrePlugin && pluginY.IsPrePlugin)
        {
            return 1;
        }

        // 同级别按 Name 排序
        return string.Compare(pluginX.Name, pluginY.Name, StringComparison.OrdinalIgnoreCase);
    }
}