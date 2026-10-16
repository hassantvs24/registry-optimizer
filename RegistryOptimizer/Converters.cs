using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RegistryOptimizer;

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}

/// <summary>Builds a semicircular gauge arc geometry (0..100 %) for the defrag view.</summary>
public sealed class GaugeArcConverter : IValueConverter
{
    private const double Cx = 110, Cy = 110, Radius = 90;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double d ? Math.Clamp(d, 0, 100) : 0;
        var theta = Math.PI - percent * Math.PI / 100.0; // sweep from 180deg down to 0deg
        var end = new Point(Cx + Radius * Math.Cos(theta), Cy + Radius * Math.Sin(theta));
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(Cx - Radius, Cy) };
        figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0,
            percent > 50, SweepDirection.Clockwise, true));
        geometry.Figures.Add(figure);
        return geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Formats byte counts as megabytes.</summary>
public sealed class BytesToMbConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? $"{bytes / 1024.0 / 1024.0:F1}" : "-";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Binds an enum to RadioButton IsChecked using the enum member name as parameter.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name && Enum.IsDefined(targetType, name))
            return Enum.Parse(targetType, name);
        return Binding.DoNothing;
    }
}
