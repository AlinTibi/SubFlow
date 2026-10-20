using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ALMARFELD.SubFlow.Core.Jobs;

namespace ALMARFELD.SubFlow.App.Services;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value switch { bool x => x, int i => i > 0, string s => !string.IsNullOrWhiteSpace(s), null => false, _ => true };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}

/// <summary>Binds a RadioButton to an enum value: IsChecked="{Binding Mode, Converter={StaticResource EnumEq}, ConverterParameter=Mkv}"</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            JobStatus.Completed => "Success",
            JobStatus.CompletedWithWarnings => "Warning",
            JobStatus.Failed => "Danger",
            JobStatus.Running => "Accent",
            JobStatus.Interrupted or JobStatus.Cancelled => "Warning",
            _ => "TextMuted",
        };
        return Application.Current.FindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is JobStatus s ? L.Get("Status_" + s) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FlowDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
