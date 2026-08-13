using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageViewer.Editing;
using ImageViewer.Imaging;

namespace ImageViewer.Ui;

/// <summary>
/// Corner panel showing what the current file is and how it was shot.
/// </summary>
/// <remarks>
/// Hidden by default and toggled with I. Built lazily for the same reason the status text is: a
/// panel the user has not asked for should not cost anything at startup.
/// </remarks>
public sealed class InfoOverlay : Border
{
    private readonly TextBlock _text;

    public InfoOverlay()
    {
        // Translucent rather than opaque so it never fully hides the image behind it.
        Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x14));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(14, 10, 16, 12);
        Margin = new Thickness(16);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = false;   // must never swallow a click meant for panning
        MaxWidth = 460;

        _text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
        };

        Child = _text;
    }

    /// <summary>Refreshes the panel for the current image.</summary>
    public void Update(
        DecodedImage? image, string? path, int index, int total,
        ViewTransform view, ExifSummary? exif)
    {
        var sb = new StringBuilder();

        if (path is not null)
        {
            sb.AppendLine(Path.GetFileName(path));
            if (total > 1) sb.AppendLine($"{index + 1} of {total} in this folder");
        }

        if (image is not null)
        {
            var megapixels = image.PixelWidth * (double)image.PixelHeight / 1_000_000;
            sb.AppendLine();
            sb.AppendLine($"{image.PixelWidth} x {image.PixelHeight}  ({megapixels:0.0} MP)");
            sb.AppendLine($"{FormatBytes(image.FileSizeBytes)}   {image.DecoderName}");
            sb.AppendLine($"Zoom {view.Zoom * 100:0}%");

            // Only mention the decode resolution when it differs, so the common case stays quiet.
            if (image.DecodeScale < 0.999)
                sb.AppendLine($"decoded at {image.Bitmap.PixelWidth} x {image.Bitmap.PixelHeight} to fit");

            if (view.HasUnsavedEdit)
                sb.AppendLine("edited - Ctrl+S to save");
        }

        if (exif is { HasAnything: true })
        {
            sb.AppendLine();
            AppendIfPresent(sb, exif.Camera);
            AppendIfPresent(sb, exif.Lens);

            // Exposure settings read best on one line, the way a camera displays them.
            var settings = new[] { exif.Exposure, exif.Aperture, exif.IsoSpeed, exif.FocalLength }
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            if (settings.Length > 0) sb.AppendLine(string.Join("   ", settings));
            AppendIfPresent(sb, exif.TakenOn);
        }

        _text.Text = sb.ToString().TrimEnd();
    }

    private static void AppendIfPresent(StringBuilder sb, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine(value);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} bytes",
    };
}
