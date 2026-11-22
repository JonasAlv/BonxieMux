using AnimatedImage.Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using System;

namespace Skua.Shared.Avalonia.Controls.Shell;

public partial class WindowTitleBar : UserControl
{
    public static readonly StyledProperty<string> TitleTextProperty =
        AvaloniaProperty.Register<WindowTitleBar, string>(nameof(TitleText), "BonxieMux");

    public const string AppIconUri = "avares://Skua.Shared.Avalonia/Assets/AppIcon.png";
    public const string ManagerIconUri = "avares://Skua.Shared.Avalonia/Assets/ManagerIcon.png";

    private static readonly Uri BuiltInIconUri = new(AppIconUri);
    private static IImage? _defaultIconSource;
    private static WindowIcon? _defaultWindowIcon;
    private static string? _artworkPath;
    private Bitmap? _artworkBitmap;
    private bool _usesInstanceArtwork;

    public static WindowIcon? DefaultWindowIcon => _defaultWindowIcon;

    public static event Action<string?>? ArtworkPathChanged;

    public static void SetArtworkPath(string? path)
    {
        _artworkPath = string.IsNullOrWhiteSpace(path) ? null : path;
        ArtworkPathChanged?.Invoke(_artworkPath);
    }

    public static bool SetDefaultIcon(string avaresUri)
    {
        try
        {
            var uri = new Uri(avaresUri);
            _defaultIconSource = LoadIcon(uri);
            using var stream = AssetLoader.Open(uri);
            _defaultWindowIcon = new WindowIcon(stream);
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => window.Icon ??= _defaultWindowIcon);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void SetInstanceArtworkPath(string? path)
    {
        _usesInstanceArtwork = true;
        LoadArtworkPath(path);
    }

    public static readonly StyledProperty<IImage?> IconSourceProperty =
        AvaloniaProperty.Register<WindowTitleBar, IImage?>(nameof(IconSource));

    public static readonly StyledProperty<bool> UseAccentBackgroundProperty =
        AvaloniaProperty.Register<WindowTitleBar, bool>(nameof(UseAccentBackground), false);

    public static readonly StyledProperty<object?> LeftContentProperty =
        AvaloniaProperty.Register<WindowTitleBar, object?>(nameof(LeftContent));

    public object? LeftContent
    {
        get => GetValue(LeftContentProperty);
        set => SetValue(LeftContentProperty, value);
    }

    public bool UseAccentBackground
    {
        get => GetValue(UseAccentBackgroundProperty);
        set => SetValue(UseAccentBackgroundProperty, value);
    }

    public string TitleText
    {
        get => GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public IImage? IconSource
    {
        get => GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? DockClicked;

    public bool CanDock
    {
        get => DockButton.IsVisible;
        set => DockButton.IsVisible = value;
    }

    public event EventHandler? TitleLayoutChanged;

    public double CenteredTitleLeftEdge
    {
        get
        {
            double titleW = TitleTextBlock.Bounds.Width > 0 ? TitleTextBlock.Bounds.Width : 76.0;
            return Math.Max(36.0, (Bounds.Width - titleW) / 2.0);
        }
    }

    public WindowTitleBar()
    {
        InitializeComponent();
        DockButton.Click += (s, e) => DockClicked?.Invoke(this, e);
        SizeChanged += (_, _) => TitleLayoutChanged?.Invoke(this, EventArgs.Empty);
        TitleTextBlock.SizeChanged += (_, _) => TitleLayoutChanged?.Invoke(this, EventArgs.Empty);
        ApplyThemeIcon();
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyAccentForeground();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == UseAccentBackgroundProperty)
                ApplyAccentForeground();
        };
        Loaded += (_, _) =>
        {
            ArtworkPathChanged -= ApplyArtworkPath;
            ArtworkPathChanged += ApplyArtworkPath;
            ApplyArtworkPath(_artworkPath);
            ApplyAccentForeground();
        };
        Unloaded += (_, _) => ArtworkPathChanged -= ApplyArtworkPath;
        PointerPressed += OnTitleBarPointerPressed;
        MinimizeButton.Click += OnMinimizeClicked;
        MaximizeButton.Click += OnMaximizeClicked;
        CloseButton.Click += OnCloseClicked;
        Loaded += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window)
                return;

            if (!window.CanResize)
                MaximizeButton.IsVisible = false;

            UpdateMaximizeGlyph(window.WindowState);
            window.PropertyChanged += OnWindowPropertyChanged;
        };
        Unloaded += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.PropertyChanged -= OnWindowPropertyChanged;
            }
        };
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState state)
            UpdateMaximizeGlyph(state);
    }

    private void UpdateMaximizeGlyph(WindowState state)
    {
        bool maximized = state == WindowState.Maximized;
        MaximizeButton.Content = new MaterialIcon
        {
            Kind = maximized ? MaterialIconKind.WindowRestore : MaterialIconKind.WindowMaximize,
            Width = 12,
            Height = 12
        };
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void ApplyThemeIcon()
    {
        IImage icon = _defaultIconSource ??= LoadIcon(BuiltInIconUri);
        if (IconSource is not null && IconSource != icon)
            return;
        SetCurrentValue(IconSourceProperty, icon);
    }

    private void ApplyAccentForeground()
    {
        string iconKey = UseAccentBackground ? "SkuaAccentForegroundBrush" : "SkuaAccentBrush";
        string titleKey = UseAccentBackground ? "SkuaAccentForegroundBrush" : "SkuaTextBrush";
        var iconDyn = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(iconKey);
        var titleDyn = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(titleKey);
        TitleTextBlock[!TextBlock.ForegroundProperty] = titleDyn;
        MinimizeButton[!Button.ForegroundProperty] = iconDyn;
        MaximizeButton[!Button.ForegroundProperty] = iconDyn;
        CloseButton[!Button.ForegroundProperty] = iconDyn;
        DockButton[!Button.ForegroundProperty] = iconDyn;
    }

    private void ApplyArtworkPath(string? path)
    {
        if (_usesInstanceArtwork)
            return;

        LoadArtworkPath(path);
    }

    private void LoadArtworkPath(string? path)
    {
        ArtworkImage.ClearValue(ImageBehavior.AnimatedSourceProperty);
        _artworkBitmap?.Dispose();
        _artworkBitmap = null;

        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            ArtworkImage.Source = null;
            return;
        }

        try
        {
            if (string.Equals(System.IO.Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                ArtworkImage.Source = null;
                ImageBehavior.SetAnimatedSource(ArtworkImage, new Uri(path, UriKind.Absolute));
                return;
            }

            _artworkBitmap = new Bitmap(path);
            ArtworkImage.Source = _artworkBitmap;
        }
        catch (Exception)
        {
            ArtworkImage.ClearValue(ImageBehavior.AnimatedSourceProperty);
            _artworkBitmap?.Dispose();
            _artworkBitmap = null;
            ArtworkImage.Source = null;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || TopLevel.GetTopLevel(this) is not Window window)
            return;

        if (IsInteractiveSource(e.Source as Visual))
            return;

        if (e.ClickCount == 2)
        {
            if (window.CanResize && MaximizeButton.IsVisible)
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        window.BeginMoveDrag(e);
    }

    private bool IsInteractiveSource(Visual? source)
    {
        for (Visual? cur = source; cur is not null && !ReferenceEquals(cur, this); cur = cur.GetVisualParent())
        {
            if (cur is Menu or MenuItem or Button or global::Avalonia.Controls.Primitives.ToggleButton or ComboBox or TextBox)
                return true;
        }
        return false;
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
            return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }

    private static IImage LoadIcon(Uri uri)
    {
        using var stream = AssetLoader.Open(uri);
        return Bitmap.DecodeToWidth(stream, 128, BitmapInterpolationMode.HighQuality);
    }
}
