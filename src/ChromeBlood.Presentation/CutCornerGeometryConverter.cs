using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ChromeBlood.Presentation;

public sealed class CutCornerGeometryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not double width || values[1] is not double height
            || width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
            return Geometry.Empty;
        double cut = Math.Min(8, Math.Min(width, height));
        PathFigure figure = new(new Point(0, 0),
        [
            new LineSegment(new Point(width, 0), true),
            new LineSegment(new Point(width, height - cut), true),
            new LineSegment(new Point(width - cut, height), true),
            new LineSegment(new Point(0, height), true)
        ], true);
        PathGeometry geometry = new([figure]);
        geometry.Freeze();
        return geometry;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
