using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ImageViewer.Ui;

/// <summary>
/// Brief confirmation message that fades away on its own.
/// </summary>
/// <remarks>
/// Actions like saving, deleting or copying need acknowledgement, but a modal dialog for each would
/// be intolerable when culling a folder. This says what happened and gets out of the way.
/// </remarks>
public sealed class Toast : Border
{
    private readonly TextBlock _text;
    private readonly DispatcherTimer _hideTimer;

    public Toast()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x18, 0x18, 0x1E));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(18, 11, 18, 12);
        Margin = new Thickness(0, 0, 0, 44);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Opacity = 0;
        MaxWidth = 640;

        _text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };

        Child = _text;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Background);
        _hideTimer.Tick += (_, _) => BeginFadeOut();
    }

    /// <summary>Shows a message for a few seconds. Errors linger longer than confirmations.</summary>
    public void Show(string message, bool isError = false)
    {
        _text.Text = message;
        BorderBrush = new SolidColorBrush(isError
            ? Color.FromArgb(0xB0, 0xFF, 0x6B, 0x6B)
            : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));

        Visibility = Visibility.Visible;

        // Snap in, so rapid actions each register rather than blurring into one another.
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(isError ? 6 : 2.2);
        _hideTimer.Start();
    }

    private void BeginFadeOut()
    {
        _hideTimer.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
        fade.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, fade);
    }
}
