using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Editing;
using ImageViewer.Imaging;

namespace ImageViewer.SelfTest;

internal static class ZoomChecks
{
    public static void Run(Action<string, bool, string?> check, string? photoPath = null, string? evidenceDir = null)
    {
        // Compare actual WPF rendering before and after replacing a fit-sized bitmap
        // with its full-resolution equivalent, at exactly the same zoom and pan.
        var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        var root = (Grid)window.Content;
        var host = (Image)Get("_imageHost");
        var view = (ViewTransform)Get("_view");
        var matrix = (MatrixTransform)Get("_matrix");
        var viewport = new Size(600, 400);
        var bytes = photoPath is null ? null : System.IO.File.ReadAllBytes(photoPath);
        var small = bytes is null ? SolidImage(600, 400) : WicDecoder.Decode(bytes, photoPath!, 600, 400, CancellationToken.None);
        var full = bytes is null ? SolidImage(6000, 4000) : WicDecoder.Decode(bytes, photoPath!, 0, 0, CancellationToken.None);
        // JPEG decode-to-fit rounds dimensions; allow up to two viewport columns
        // for its subpixel edge shift. Synthetic regression images must match exactly.
        var tolerance = bytes is null ? 0 : 1200;
        view.ResolveZoom(small, viewport, 1);
        for (var i = 0; i < 3; i++) view.ZoomAt(1.15, new Point(300, 200), small, viewport, 1);

        int RenderVisiblePixels(DecodedImage image, string label)
        {
            host.Visibility = Visibility.Visible;
            host.Source = image.Bitmap;
            host.Width = image.Bitmap.PixelWidth;
            host.Height = image.Bitmap.PixelHeight;
            matrix.Matrix = view.BuildMatrix(image, viewport, 1);
            root.Measure(viewport);
            root.Arrange(new Rect(viewport));
            root.UpdateLayout();
            var rendered = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(root);
            if (evidenceDir is not null)
            {
                System.IO.Directory.CreateDirectory(evidenceDir);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rendered));
                using var file = System.IO.File.Create(System.IO.Path.Combine(evidenceDir, label + ".png"));
                encoder.Save(file);
            }
            var pixels = new byte[600 * 400 * 4];
            rendered.CopyPixels(pixels, 600 * 4, 0);
            var red = 0;
            for (var p = 0; p < pixels.Length; p += 4)
                if (pixels[p + 3] > 240) red++;
            return red;
        }

        try
        {
            var before = RenderVisiblePixels(small, "zoom-preview");
            var after = RenderVisiblePixels(full, "zoom-full");
            check("full-resolution zoom preserves the visible image after three wheel steps",
                before > 0 && Math.Abs(after - before) <= tolerance, $"visible pixels: before={before}, after={after}");
            view.Rotate(90);
            view.Pan(80, -50, full, viewport, 1);
            before = RenderVisiblePixels(small, "rotated-preview");
            after = RenderVisiblePixels(full, "rotated-full");
            check("rotated and panned image is not clipped by the layout slot",
                before > 0 && Math.Abs(after - before) <= tolerance, $"visible pixels: before={before}, after={after}");
        }
        finally { ((IDisposable)Get("_pipeline")).Dispose(); }
    }

    private static DecodedImage SolidImage(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8,
            new BitmapPalette(new[] { Colors.Red }), new byte[width * height], width);
        bitmap.Freeze();
        return new DecodedImage { Bitmap = bitmap, PixelWidth = 6000, PixelHeight = 4000,
            DecoderName = "test", Path = "zoom-test.png" };
    }
}
