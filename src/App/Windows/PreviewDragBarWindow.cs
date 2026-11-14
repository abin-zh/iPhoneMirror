using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

/// <summary>
/// Slim hover-revealed title bar for the borderless DirectComposition preview.
/// The preview HWND cannot host WPF content (WS_EX_NOREDIRECTIONBITMAP), so the
/// bar is an owned, non-activating window that drives the preview through
/// Win32 messages instead of owning any of its own window state.
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
    private const double BarHeightDips = 26;

    private static readonly Brush BarBackground = Frozen(new SolidColorBrush(
        Color.FromArgb(0xE6, 0x1B, 0x1C, 0x1E)));
    private static readonly Brush ButtonHoverBackground = Frozen(new SolidColorBrush(
        Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush IconBrush = Frozen(new SolidColorBrush(
        Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));

    private readonly Func<nint> _ownerHandle;
    private readonly Action _drag;
    private readonly Func<bool> _isFullScreen;
    private readonly Action<string>? _log;
    private readonly Border _fullScreenButton;
    private nint _handle;

    internal PreviewDragBarWindow(Func<nint> ownerHandle, Action drag,
        Action toggleFullScreen, Func<bool> isFullScreen, Action<string>? log = null)
    {
        _ownerHandle = ownerHandle;
        _drag = drag;
        _isFullScreen = isFullScreen;
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

        _fullScreenButton = new Border
        {
            Width = 26,
            Height = 20,
            Margin = new Thickness(2, 0, 5, 0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                // Four corner brackets; no icon-font dependency.
                Data = Geometry.Parse(
                    "M0,5 L0,0 L5,0 M11,0 L16,0 L16,5 M16,11 L16,16 L11,16 M5,16 L0,16 L0,11"),
                Stroke = IconBrush,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _fullScreenButton.MouseEnter += (_, _) =>
        {
            _fullScreenButton.Background = ButtonHoverBackground;
            HoverEntered?.Invoke();
        };
        _fullScreenButton.MouseLeave += (_, _) =>
        {
            _fullScreenButton.Background = Brushes.Transparent;
            HoverLeft?.Invoke();
        };
        _fullScreenButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            toggleFullScreen();
            RefreshTooltip();
        };
        RefreshTooltip();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(dragArea);
        Grid.SetColumn(_fullScreenButton, 1);
        grid.Children.Add(_fullScreenButton);

        Content = new Border
        {
            Background = BarBackground,
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Child = grid,
        };
        Width = 320;
        Height = BarHeightDips;

        var owner = _ownerHandle();
        if (owner != 0) _ = new WindowInteropHelper(this) { Owner = owner };
        SourceInitialized += OnSourceInitialized;
        Log("drag_bar_created");
    }

    internal event Action? HoverEntered;
    internal event Action? HoverLeft;

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void RefreshTooltip()
    {
        _fullScreenButton.ToolTip = LocalizationService.Get(
            _isFullScreen()
                ? "IndependentWindowExitFullScreen"
                : "IndependentWindowEnterFullScreen");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        if (_handle == 0) return;
        var style = GetWindowLongPtrW(_handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtrW(_handle, GwlExStyle,
            (nint)(style | WsExNoActivate | WsExToolWindow));
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProcedure);
        Log("drag_bar_source_initialized");
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

    /// <summary>Aligns the bar with the current preview bounds, in physical pixels.</summary>
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
        var inset = Math.Max(0, (int)Math.Round(2 * scale));
        var height = Math.Max(1, (int)Math.Round(BarHeightDips * scale));
        var width = Math.Max(1, bounds.Right - bounds.Left - inset * 2);
        _ = SetWindowPos(_handle, 0, bounds.Left + inset, bounds.Top + inset,
            width, height, SwpNoActivate | SwpNoZOrder | SwpShowWindow);
    }

    internal void ShowOver(nint owner)
    {
        if (owner == 0) return;
        if (!IsVisible) Show();
        // Show() may activate on some shell configurations; reassert the style
        // and jump straight back to the preview's z-order band.
        if (_handle != 0)
        {
            var style = GetWindowLongPtrW(_handle, GwlExStyle).ToInt64();
            _ = SetWindowLongPtrW(_handle, GwlExStyle,
                (nint)(style | WsExNoActivate | WsExToolWindow));
        }
        PositionOver(owner);
        Log("drag_bar_shown");
    }

    internal void HideBar()
    {
        if (!IsVisible) return;
        Hide();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
