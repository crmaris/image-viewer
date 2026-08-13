using System.Windows;
using System.Windows.Media;
using ImageViewer.Imaging;

namespace ImageViewer.Editing;

public enum ZoomMode
{
    /// <summary>Scale down to fit the window; never enlarge beyond 100%.</summary>
    Fit,
    /// <summary>One image pixel to one physical screen pixel.</summary>
    Actual,
    /// <summary>An explicit zoom the user dialled in.</summary>
    Free,
}

/// <summary>
/// Rotation, flip, zoom and pan state for the current image, and the matrix that realises it.
/// </summary>
/// <remarks>
/// <para>
/// Zoom is expressed in <em>physical screen pixels per original image pixel</em>. Working in
/// physical pixels rather than WPF's device-independent units is what makes "100%" honest on a
/// 150%-scaled display, and it stays correct when the decoder returns a downscaled bitmap, because
/// <see cref="DecodedImage.DecodeScale"/> is divided back out when the matrix is built.
/// </para>
/// </remarks>
public sealed class ViewTransform
{
    /// <summary>Extra rotation applied by the user, on top of any baked-in EXIF orientation.</summary>
    public int RotationDegrees { get; private set; }

    public bool FlipHorizontal { get; private set; }
    public bool FlipVertical { get; private set; }

    public ZoomMode Mode { get; private set; } = ZoomMode.Fit;

    /// <summary>Physical screen pixels per original image pixel.</summary>
    public double Zoom { get; private set; } = 1.0;

    public double PanX { get; private set; }
    public double PanY { get; private set; }

    /// <summary>True when the user has changed anything that a save would need to write out.</summary>
    public bool HasUnsavedEdit => RotationDegrees != 0 || FlipHorizontal || FlipVertical;

    public const double MinZoom = 0.01;
    public const double MaxZoom = 64.0;

    /// <summary>Resets everything, as when moving to a different image.</summary>
    public void Reset()
    {
        RotationDegrees = 0;
        FlipHorizontal = false;
        FlipVertical = false;
        Mode = ZoomMode.Fit;
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
    }

    /// <summary>Resets only the view, keeping rotation and flips (used after a save).</summary>
    public void ResetEdits()
    {
        RotationDegrees = 0;
        FlipHorizontal = false;
        FlipVertical = false;
    }

    public void Rotate(int deltaDegrees)
    {
        RotationDegrees = ((RotationDegrees + deltaDegrees) % 360 + 360) % 360;
        // A quarter turn changes which axis constrains the fit, so recentre rather than leaving
        // the image hanging off the edge at its old pan offset.
        PanX = 0;
        PanY = 0;
    }

    public void ToggleFlipHorizontal()
    {
        // While quarter-turned the on-screen horizontal axis is the image's vertical one, so the
        // user's "flip horizontal" has to be recorded against the axis they actually see.
        if (RotationDegrees is 90 or 270) FlipVertical = !FlipVertical;
        else FlipHorizontal = !FlipHorizontal;
    }

    public void ToggleFlipVertical()
    {
        if (RotationDegrees is 90 or 270) FlipHorizontal = !FlipHorizontal;
        else FlipVertical = !FlipVertical;
    }

    public void SetFit()
    {
        Mode = ZoomMode.Fit;
        PanX = 0;
        PanY = 0;
    }

    public void SetActualSize()
    {
        Mode = ZoomMode.Actual;
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
    }

    /// <summary>
    /// Applies the current mode, recomputing a fit zoom against the live viewport.
    /// </summary>
    public void ResolveZoom(DecodedImage image, Size viewportDip, double dpiScale)
    {
        Zoom = Mode switch
        {
            ZoomMode.Fit => ComputeFitZoom(image, viewportDip, dpiScale),
            ZoomMode.Actual => 1.0,
            _ => Math.Clamp(Zoom, MinZoom, MaxZoom),
        };
    }

    /// <summary>
    /// Largest zoom that shows the whole image, capped at 100%.
    /// </summary>
    /// <remarks>
    /// The cap is deliberate: blowing a 200 px icon up to fill a 4K window looks broken. Small
    /// images sit at their natural size and only enlarge when the user explicitly zooms.
    /// </remarks>
    public double ComputeFitZoom(DecodedImage image, Size viewportDip, double dpiScale)
    {
        var (w, h) = RotatedExtent(image);
        if (w <= 0 || h <= 0) return 1.0;

        var viewportPhysicalW = Math.Max(1.0, viewportDip.Width * dpiScale);
        var viewportPhysicalH = Math.Max(1.0, viewportDip.Height * dpiScale);

        var fit = Math.Min(viewportPhysicalW / w, viewportPhysicalH / h);
        return Math.Clamp(Math.Min(fit, 1.0), MinZoom, MaxZoom);
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> while keeping the image point under
    /// <paramref name="anchorDip"/> pinned to that same screen position.
    /// </summary>
    public void ZoomAt(
        double factor, Point anchorDip, DecodedImage image, Size viewportDip, double dpiScale)
    {
        ResolveZoom(image, viewportDip, dpiScale);

        var before = BuildMatrix(image, viewportDip, dpiScale);
        if (!before.HasInverse) return;

        var inverse = before;
        inverse.Invert();
        var imagePoint = inverse.Transform(anchorDip);

        var target = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(target - Zoom) < double.Epsilon) return;

        Zoom = target;
        Mode = ZoomMode.Free;

        // Re-place the pan so the anchored image point lands back under the cursor.
        var after = BuildMatrix(image, viewportDip, dpiScale);
        var moved = after.Transform(imagePoint);
        PanX += anchorDip.X - moved.X;
        PanY += anchorDip.Y - moved.Y;

        ClampPan(image, viewportDip, dpiScale);
    }

    public void Pan(double deltaXDip, double deltaYDip, DecodedImage image, Size viewportDip, double dpiScale)
    {
        PanX += deltaXDip;
        PanY += deltaYDip;
        ClampPan(image, viewportDip, dpiScale);
    }

    /// <summary>
    /// Stops the image being dragged off-screen: axes smaller than the viewport stay centred,
    /// larger ones stop when their edge reaches the viewport edge.
    /// </summary>
    public void ClampPan(DecodedImage image, Size viewportDip, double dpiScale)
    {
        var (w, h) = RotatedExtent(image);
        var renderedW = w * Zoom / dpiScale;
        var renderedH = h * Zoom / dpiScale;

        PanX = ClampAxis(PanX, renderedW, viewportDip.Width);
        PanY = ClampAxis(PanY, renderedH, viewportDip.Height);

        static double ClampAxis(double pan, double rendered, double viewport)
        {
            if (rendered <= viewport) return 0;
            var limit = (rendered - viewport) / 2.0;
            return Math.Clamp(pan, -limit, limit);
        }
    }

    /// <summary>
    /// Builds the image-space to viewport-space matrix.
    /// </summary>
    /// <remarks>
    /// Operations compose in this order: recentre on the image's middle, mirror, rotate, scale to
    /// the requested zoom, then translate to the viewport centre plus the pan offset. Doing the
    /// mirror before the rotation is what keeps flip-then-rotate behaving the way a user expects.
    /// </remarks>
    public Matrix BuildMatrix(DecodedImage image, Size viewportDip, double dpiScale)
    {
        var bw = image.Bitmap.PixelWidth;
        var bh = image.Bitmap.PixelHeight;

        // The Image element is laid out at the decoded bitmap's pixel size in DIPs, so undoing
        // DecodeScale here is what lets a downscaled decode still render at the requested zoom.
        var scale = Zoom / (image.DecodeScale * dpiScale);

        var m = Matrix.Identity;
        m.Translate(-bw / 2.0, -bh / 2.0);

        if (FlipHorizontal || FlipVertical)
            m.Scale(FlipHorizontal ? -1 : 1, FlipVertical ? -1 : 1);

        if (RotationDegrees != 0)
            m.Rotate(RotationDegrees);

        m.Scale(scale, scale);
        m.Translate(viewportDip.Width / 2.0 + PanX, viewportDip.Height / 2.0 + PanY);

        return m;
    }

    /// <summary>Original-pixel extent after the user's rotation, with axes swapped on a quarter turn.</summary>
    public (double Width, double Height) RotatedExtent(DecodedImage image) =>
        RotationDegrees is 90 or 270
            ? (image.PixelHeight, image.PixelWidth)
            : (image.PixelWidth, image.PixelHeight);
}
