using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageViewer.Ui;

/// <summary>
/// Small modal prompt for renaming the current file.
/// </summary>
/// <remarks>
/// Built in code to match the rest of the window, and styled dark so it does not flash a white
/// dialog over a photo. Opens with the base name selected but the extension left alone, which is
/// what Explorer does and what makes renaming a series quick.
/// </remarks>
public static class RenameDialog
{
    /// <summary>Prompts for a new file name. Returns null if cancelled.</summary>
    public static string? Ask(Window owner, string currentName)
    {
        var dialog = new Window
        {
            Title = "Rename",
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22)),
        };

        var box = new TextBox
        {
            Text = currentName,
            FontSize = 14,
            Padding = new Thickness(7, 6, 7, 6),
            Margin = new Thickness(0, 0, 0, 14),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x5A)),
            BorderThickness = new Thickness(1),
        };

        var ok = new Button
        {
            Content = "Rename",
            IsDefault = true,
            Padding = new Thickness(18, 5, 18, 5),
            MinWidth = 92,
        };

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(18, 5, 18, 5),
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            var text = box.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            result = text;
            dialog.DialogResult = true;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(new TextBlock
        {
            Text = "New file name:",
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC8)),
            Margin = new Thickness(0, 0, 0, 7),
        });
        layout.Children.Add(box);
        layout.Children.Add(buttons);

        dialog.Content = layout;

        dialog.Loaded += (_, _) =>
        {
            box.Focus();

            // Select the name but not the extension, so typing replaces the part being changed
            // without silently destroying the file type.
            var stem = Path.GetFileNameWithoutExtension(currentName);
            box.Select(0, stem.Length > 0 ? stem.Length : currentName.Length);
        };

        // Escape closes even when focus is inside the text box.
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.DialogResult = false;
        };

        return dialog.ShowDialog() == true ? result : null;
    }
}
