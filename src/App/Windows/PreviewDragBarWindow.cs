using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// Windows 11 style title bar for the borderless DirectComposition preview.
/// The preview HWND cannot host WPF content (WS_EX_NOREDIRECTIONBITMAP) and its
/// rounded corners live inside the client area, so the bar is an owned,
/// non-activating window that floats just above the picture and drives the
/// preview through Win32 messages.
/// </summary>
internal sealed class PreviewDragBarWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;
    private const double BarHeightDips = 32;
    private const double BarGapDips = 8;
    private const double ButtonWidthDips = 38;
    private const double ButtonHeightDips = 28;
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(90);

    private static readonly Brush BarBackground = Frozen(new SolidColorBrush(
        Color.FromArgb(0xF2, 0x20, 0x20, 0x20)));
    private static readonly Brush BarBorder = Frozen(new SolidColorBrush(
        Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ButtonHover = Frozen(new SolidColorBrush(
        Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CloseHover = Frozen(new SolidColorBrush(
        Color.FromRgb(0xC4, 0x2B, 0x1C)));
    private static readonly Brush IconBrush = Frozen(new SolidColorBrush(
        Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GlyphStroke = Frozen(new SolidColorBrush(
        Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));

    private readonly Func<nint> _ownerHandle;
    private readonly Action _drag;
    private readonly Action _minimize;
    private readonly Action _close;
    private readonly Action<string>? _log;
    private readonly Border _surface;
    private readonly TranslateTransform _slide = new();
    private readonly Border _fullScreenButton;
    private nint _handle;
    private bool _hiding;

    internal PreviewDragBarWindow(Func<nint> ownerHandle, Action drag, Action minimize,
        Action toggleFullScreen, Action close, Action<string>? log = null)
    {
        _ownerHandle = ownerHandle;
        _drag = drag;
        _minimize = minimize;
        _close = close;
        _log = log;

        Title = "iPhoneMirror preview bar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = 0;
        MinHeight = 0;

        var dragArea = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
        };
        dragArea.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _drag();
        };
        dragArea.MouseEnter += (_, _) => HoverEntered?.Invoke();
        dragArea.MouseLeave += (_, _) => HoverLeft?.Invoke();

        var minimizeButton = CreateCaptionButton(CaptionGlyphKind.Minimize, hoverOnClose: false);
        minimizeButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _minimize();
        };

        _fullScreenButton = CreateCaptionButton(CaptionGlyphKind.FullScreen, hoverOnClose: false);
        _fullScreenButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            toggleFullScreen();
        };

        var closeButton = CreateCaptionButton(CaptionGlyphKind.Close, hoverOnClose: true);
        closeButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _close();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.Children.Add(dragArea);
        foreach (var (button, index) in new[] { minimizeButton, _fullScreenButton, closeButton }
            .Select((button, index) => (button, index)))
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(button, index + 1);
            grid.Children.Add(button);
        }

        _surface = new Border
        {
            Background = BarBackground,
            BorderBrush = BarBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = grid,
            RenderTransform = _slide,
        };
        Content = _surface;
        Width = 320;
        Height = BarHeightDips;

        var owner = _ownerHandle();
        if (owner != 0) _ = new WindowInteropHelper(this) { Owner = owner };
        SourceInitialized += OnSourceInitialized;
        Log("drag_bar_created");
    }

    private enum CaptionGlyphKind
    {
        Minimize,
        FullScreen,
        Close,
    }

    internal event Action? HoverEntered;
    internal event Action? HoverLeft;

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Border CreateCaptionButton(CaptionGlyphKind kind, bool hoverOnClose)
    {
        var stroke = kind == CaptionGlyphKind.Close ? GlyphStroke : IconBrush;
        var geometry = kind switch
        {
            CaptionGlyphKind.Minimize => Geometry.Parse("M0,5 L10,5"),
            CaptionGlyphKind.Close => Geometry.Parse("M0.5,0.5 L9.5,9.5 M9.5,0.5 L0.5,9.5"),
            _ => Geometry.Parse("M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z"),
        };
        var glyph = new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = kind == CaptionGlyphKind.Minimize ? 1.2 : 1.0,
            StrokeLineJoin = PenLineJoin.Miter,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        var button = new Border
        {
            Width = ButtonWidthDips,
            Height = ButtonHeightDips,
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = glyph,
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = hoverOnClose ? CloseHover : ButtonHover;
            if (hoverOnClose) glyph.Stroke = Brushes.White;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            if (hoverOnClose) glyph.Stroke = GlyphStroke;
        };
        return button;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        if (_handle == 0) return;
        ApplyNonActivatingStyle();
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProcedure);
        Log("drag_bar_source_initialized");
    }

    private void ApplyNonActivatingStyle()
    {
        if (_handle == 0) return;
        var style = GetWindowLongPtrW(_handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtrW(_handle, GwlExStyle,
            (nint)(style | WsExNoActivate | WsExToolWindow));
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        if (message != WmMouseActivate) return 0;
        // Clicking the bar must never move keyboard ownership away from the
        // preview: forward-control keyboard only follows the foreground HWND.
        handled = true;
        return MaNoActivate;
    }

    /// <summary>
    /// Aligns the bar just above the picture, in physical pixels. When the
    /// preview is already touching the top of its work area the bar drops back
    /// inside the window so it can never end up off-screen.
    /// </summary>
    internal void PositionOver(nint owner)
    {
        if (owner == 0) return;
        if (_handle == 0)
        {
            Width = 320;
            Height = BarHeightDips;
            return;
        }
        if (!GetWindowRect(owner, out var bounds)) return;
        var dpi = GetDpiForWindow(owner);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var height = Math.Max(1, (int)Math.Round(BarHeightDips * scale));
        var gap = Math.Max(1, (int)Math.Round(BarGapDips * scale));
        var width = Math.Max(1, bounds.Right - bounds.Left);
        var top = bounds.Top - height - gap;
        var monitor = MonitorFromWindow(owner, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfoW(monitor, ref info))
        {
            var limit = info.WorkArea.Top;
            if (top < limit) top = Math.Min(bounds.Top, limit);
        }
        else if (top < 0)
        {
            top = Math.Max(0, bounds.Top);
        }
        _ = SetWindowPos(_handle, 0, bounds.Left, top, width, height,
            SwpNoActivate | SwpNoZOrder | SwpShowWindow);
    }

    internal void ShowOver(nint owner)
    {
        if (owner == 0) return;
        _hiding = false;
        PositionOver(owner);
        if (IsVisible) return;
        Show();
        ApplyNonActivatingStyle();
        PositionOver(owner);
        AnimateIn();
        Log("drag_bar_shown");
    }

    private void AnimateIn()
    {
        BeginAnimation(OpacityProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 0;
        _slide.Y = -8;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ShowDuration)
        {
            EasingFunction = ease,
        });
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, ShowDuration) { EasingFunction = ease });
    }

    internal void HideBar()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Opacity, 0, HideDuration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            _hiding = false;
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            _slide.Y = 0;
        };
        BeginAnimation(OpacityProperty, fade);
        Log("drag_bar_hidden");
    }

    internal bool ContainsScreenPoint(int x, int y) =>
        _handle != 0 && IsVisible && GetWindowRect(_handle, out var bounds) &&
        x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;

    private void Log(string eventName)
    {
        if (_log is null) return;
        try { _log(AppLog.Event(eventName)); }
        catch { /* Diagnostics must never break the drag bar. */ }
    }

    private static nint GetWindowLongPtrW(nint hwnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    private static nint SetWindowLongPtrW(nint hwnd, int index, nint value) =>
        nint.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : SetWindowLong32(hwnd, index, value.ToInt32());

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern nint GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern nint SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal WindowRect Monitor;
        internal WindowRect WorkArea;
        internal uint Flags;
    }
}
