using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace LocalProjectBridge;

/// <summary>Cached shell images; native icon handles are released immediately after cloning.</summary>
internal sealed class ConnectionStatusIcon : IDisposable
{
    public Drawing.Icon TrayIcon { get; }
    public ImageSource WindowIcon { get; }
    public ImageSource Overlay { get; }

    public ConnectionStatusIcon(Drawing.Icon original, string color)
    {
        var tint = (Color)ColorConverter.ConvertFromString(color);
        using var source = original.ToBitmap();
        using var bitmap = new Drawing.Bitmap(source, 32, 32);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A == 0) continue;
            // Keep the bright link symbol while tinting the application's blue silhouette.
            var light = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) / 255d;
            byte Mix(byte component) => (byte)(component + (255 - component) * light);
            bitmap.SetPixel(x, y, Drawing.Color.FromArgb(pixel.A, Mix(tint.R), Mix(tint.G), Mix(tint.B)));
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle);
            TrayIcon = (Drawing.Icon)borrowed.Clone();
            WindowIcon = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            WindowIcon.Freeze();
        }
        finally { DestroyIcon(handle); }
        var dot = new GeometryDrawing(new SolidColorBrush(tint), new Pen(Brushes.White, 1.5),
            new EllipseGeometry(new Point(8, 8), 6.5, 6.5));
        Overlay = new DrawingImage(dot);
        Overlay.Freeze();
    }

    public void Dispose() => TrayIcon.Dispose();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
