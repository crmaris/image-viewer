using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Ui;

/// <summary>
/// Bottom strip of thumbnails for jumping around a folder.
/// </summary>
/// <remarks>
/// <para>
/// Thumbnails are produced on a background thread, one at a time, and only for entries that are
/// actually scrolled into view. A folder of several thousand images must not cost anything until
/// the strip is opened, and must never compete with decoding the image the user is looking at.
/// </para>
/// <para>
/// Hidden by default and toggled with T.
/// </para>
/// </remarks>
public sealed class Filmstrip : Border
{
    private const int ThumbnailHeight = 78;
    private const int ThumbnailMaxWidth = 130;

    private readonly ListBox _list;
    private readonly SemaphoreSlim _throttle = new(1);
    private CancellationTokenSource? _generation;

    /// <summary>Raised when the user picks a thumbnail.</summary>
    public event Action<int>? IndexSelected;

    public Filmstrip()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0E, 0x0E, 0x12));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(0, 1, 0, 0);
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Visibility = Visibility.Collapsed;
        Height = ThumbnailHeight + 26;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        // Attached properties cannot go in an object initialiser.
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        // Horizontal layout, virtualised so a huge folder does not realise every item at once.
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
        _list.ItemsPanel = new ItemsPanelTemplate(panel);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);

        _list.ItemTemplate = BuildItemTemplate();
        _list.SelectionChanged += OnSelectionChanged;

        Child = _list;
    }

    private static DataTemplate BuildItemTemplate()
    {
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Thumb.Source)));
        image.SetValue(Image.HeightProperty, (double)ThumbnailHeight);
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.SetValue(FrameworkElement.MaxWidthProperty, (double)ThumbnailMaxWidth);
        image.SetValue(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(Thumb.Name)));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.PaddingProperty, new Thickness(3));
        border.SetValue(Border.MinWidthProperty, 40.0);
        border.AppendChild(image);

        return new DataTemplate { VisualTree = border };
    }

    /// <summary>Repopulates the strip for a new folder.</summary>
    public void SetFiles(string[] files)
    {
        CancelGeneration();

        var items = files.Select(f => new Thumb(f)).ToList();
        _list.ItemsSource = items;

        if (Visibility == Visibility.Visible) StartGenerating(items);
    }

    /// <summary>Highlights and scrolls to the current image, without re-raising selection.</summary>
    public void SetCurrentIndex(int index)
    {
        if (_list.ItemsSource is not List<Thumb> items) return;
        if (index < 0 || index >= items.Count) return;
        if (_list.SelectedIndex == index) return;

        _list.SelectionChanged -= OnSelectionChanged;
        _list.SelectedIndex = index;
        _list.ScrollIntoView(items[index]);
        _list.SelectionChanged += OnSelectionChanged;
    }

    public void Toggle()
    {
        var showing = Visibility != Visibility.Visible;
        Visibility = showing ? Visibility.Visible : Visibility.Collapsed;

        if (showing && _list.ItemsSource is List<Thumb> items) StartGenerating(items);
        else CancelGeneration();
    }

    /// <summary>
    /// Fills in thumbnails in the background, nearest the current selection first.
    /// </summary>
    private async void StartGenerating(List<Thumb> items)
    {
        CancelGeneration();
        _generation = new CancellationTokenSource();
        var ct = _generation.Token;

        var start = Math.Max(0, _list.SelectedIndex);

        // Work outwards from where the user is looking, so the visible part fills first.
        var order = Enumerable.Range(0, items.Count)
            .OrderBy(i => Math.Abs(i - start))
            .ToArray();

        try
        {
            foreach (var i in order)
            {
                if (ct.IsCancellationRequested) return;
                if (items[i].Source is not null) continue;

                await _throttle.WaitAsync(ct).ConfigureAwait(true);
                try
                {
                    var path = items[i].Path;
                    var thumbnail = await Task.Run(() => Generate(path, ct), ct).ConfigureAwait(true);
                    if (!ct.IsCancellationRequested) items[i].Source = thumbnail;
                }
                finally
                {
                    _throttle.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The strip was closed or the folder changed.
        }
    }

    /// <summary>
    /// Builds one thumbnail, preferring the file's embedded preview.
    /// </summary>
    /// <remarks>
    /// DecodePixelHeight makes the codec produce a small image directly rather than decoding a
    /// 24 MP frame and shrinking it, which is the difference between a strip that fills in
    /// smoothly and one that pins a core for a minute.
    /// </remarks>
    private static BitmapSource? Generate(string path, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = ThumbnailHeight * 2;   // 2x for high-DPI displays
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A file the strip cannot thumbnail simply shows an empty slot; the main view will
            // report the real error if the user navigates to it.
            return null;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_list.SelectedIndex >= 0) IndexSelected?.Invoke(_list.SelectedIndex);
    }

    private void CancelGeneration()
    {
        _generation?.Cancel();
        _generation?.Dispose();
        _generation = null;
    }

    /// <summary>One entry in the strip. Notifies so a thumbnail can appear once it is ready.</summary>
    private sealed class Thumb(string path) : System.ComponentModel.INotifyPropertyChanged
    {
        private BitmapSource? _source;

        public string Path { get; } = path;
        public string Name { get; } = System.IO.Path.GetFileName(path);

        public BitmapSource? Source
        {
            get => _source;
            set
            {
                _source = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Source)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
