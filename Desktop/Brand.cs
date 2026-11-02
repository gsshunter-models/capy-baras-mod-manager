using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MXSimModManager.Desktop;

public static class Brand
{
    public const string Name = "Capy Bara's Mod Manager";
    public static readonly Brush Background = ColorBrush("#14120F");
    public static readonly Brush Panel = ColorBrush("#211E18");
    public static readonly Brush Text = ColorBrush("#F5EFDF");
    public static readonly Brush Muted = ColorBrush("#B6AA98");
    public static readonly Brush Accent = ColorBrush("#FFAB49");
    public static readonly Brush AccentText = ColorBrush("#2D1C0B");
    public static readonly Brush Border = ColorBrush("#40372A");
    public static ImageSource Icon => BitmapFrame.Create(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
    private static Brush ColorBrush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
}
