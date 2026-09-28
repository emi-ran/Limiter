using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Limiter;

internal static class AppIcons
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    internal static ImageSource Default { get; } = CreateDefault();

    internal static Task<ImageSource> GetAsync(string path) => Cache.GetOrAdd(path,
        key => new Lazy<Task<ImageSource>>(() => Task.Run(() => Read(key)))).Value;

    private static ImageSource Read(string path)
    {
        nint large = 0, small = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return Default;
            ExtractIconEx(path, 0, out large, out small, 1);
            nint handle = small != 0 ? small : large;
            if (handle == 0) return Default;
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch { return Default; }
        finally
        {
            if (small != 0) DestroyIcon(small);
            if (large != 0 && large != small) DestroyIcon(large);
        }
    }

    private static ImageSource CreateDefault()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(72, 82, 94)), null,
            new RectangleGeometry(new Rect(0, 1, 16, 14), 2, 2)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(142, 157, 175)), null,
            new RectangleGeometry(new Rect(2, 3, 12, 2))));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
