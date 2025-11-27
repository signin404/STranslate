using STranslate.Plugin;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace STranslate.Converters;

public class ServiceVisibilityConverter : MarkupExtension, IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values.Any(v => v == DependencyProperty.UnsetValue))
            return Visibility.Collapsed;

        var isEnabled = (bool)values[0];
        var execMode = (ExecutionMode)values[1];
        var isTemporaryDisplay = (bool)values[2];

        // 当 IsEnabled 为 true 且 ExecutionMode 不为 Pinned 时可见
        if (isEnabled && (isTemporaryDisplay || execMode != ExecutionMode.Pinned))
        {
            return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => Array.Empty<object>();

    public override object ProvideValue(IServiceProvider serviceProvider)
        => this;
}

public class ServicePinnedVisibilityConverter : MarkupExtension, IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values.Any(v => v == DependencyProperty.UnsetValue))
            return Visibility.Collapsed;

        var isEnabled = (bool)values[0];
        var execMode = (ExecutionMode)values[1];
        var isTemporaryDisplay = (bool)values[2];

        // 当 IsEnabled 为 true 且 ExecutionMode 为 Pinned 时可见
        if (isEnabled && execMode == ExecutionMode.Pinned && !isTemporaryDisplay)
        {
            return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => Array.Empty<object>();

    public override object ProvideValue(IServiceProvider serviceProvider)
        => this;
}
