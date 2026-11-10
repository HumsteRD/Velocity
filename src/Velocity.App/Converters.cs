using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Velocity.Core;
using Velocity.Tweaks;

namespace Velocity.App;

/// <summary>Цвет по серьёзности находки.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            FindingSeverity.Critical => "Critical",
            FindingSeverity.Warning => "Warning",
            _ => "Info"
        };
        return Application.Current.Resources[key] ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class SeverityLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FindingSeverity.Critical => Loc.T("Критично"),
        FindingSeverity.Warning => Loc.T("Важно"),
        _ => Loc.T("К сведению")
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class RiskLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TweakRisk.Safe => Loc.T("безопасно"),
        TweakRisk.Moderate => Loc.T("умеренно"),
        TweakRisk.Advanced => Loc.T("продвинуто"),
        TweakRisk.Expert => Loc.T("с риском"),
        _ => ""
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class RiskBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            TweakRisk.Safe => "Success",
            TweakRisk.Moderate => "Info",
            TweakRisk.Advanced => "Warning",
            TweakRisk.Expert => "Critical",
            _ => "TextTertiary"
        };
        return Application.Current.Resources[key] ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        // Параметр «invert» переворачивает условие — избавляет от второго конвертера.
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value is not null && !(value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter as string == "invert") present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool any = value is int n && n > 0;
        if (parameter as string == "invert") any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
