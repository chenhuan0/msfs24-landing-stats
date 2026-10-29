using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

/// <summary>
/// Shows the touchdown card over the simulator for a few seconds. The window is
/// click-through and never takes focus, so simulator input is not interrupted.
/// It is drawn above other windows, so it cannot appear in VR or over an
/// exclusive full-screen swap chain.
/// </summary>
internal sealed class TouchdownCardWindow : Window
{
    internal static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(8);
    /// <summary>Gap between a corner card and the monitor edges, as a fraction of the monitor height.</summary>
    private const double CornerFraction = .03;
    /// <summary>Gap above a top-centre card, clear of the simulator's toolbar.</summary>
    private const double TopCenterFraction = .08;
    /// <summary>Gap below a bottom-centre card.</summary>
    private const double BottomCenterFraction = .1;
    private readonly DispatcherTimer _timer;
    private readonly TranslateTransform _offset = new TranslateTransform();
    private readonly TouchdownCardView _view = new TouchdownCardView();

    public TouchdownCardWindow()
    {
        Title = "MSFS Landing Stats";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        // Later language changes reach open windows through LocalizationManager.Apply.
        Language = LocalizationManager.DisplayLanguage;
        _view.RenderTransform = _offset;
        Content = _view;
        _timer = new DispatcherTimer { Interval = DisplayDuration };
        _timer.Tick += (_, _) => Dismiss();
        SourceInitialized += (_, _) => MakeOverlay();
    }

    public void Present(TouchdownCard card, TouchdownCardPosition position)
    {
        _view.SetCard(card);
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        UpdateLayout();
        PlaceOnSimulatorMonitor(position);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        // Slide in from the nearest monitor edge.
        var from = position == TouchdownCardPosition.BottomCenter ? 14 : -14;
        _offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        _timer.Start();
    }

    private void Dismiss()
    {
        _timer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        // A card presented during the fade restarts the timer and must stay visible.
        fade.Completed += (_, _) => { if (!_timer.IsEnabled) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void MakeOverlay()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, ExtendedStyle).ToInt64();
        SetWindowLongPtr(handle, ExtendedStyle, new IntPtr(style | TransparentStyle | ToolWindowStyle | NoActivateStyle));
    }

    /// <summary>Places the card on the monitor holding the foreground window, normally the simulator.</summary>
    private void PlaceOnSimulatorMonitor(TouchdownCardPosition position)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var window)) return;
        var monitor = MonitorFromWindow(GetForegroundWindow(), DefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        // Align the visible card, not its transparent shadow margin, with the monitor edges.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var shadow = _view.Margin;
        var (x, y) = Place(position, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom,
            window.Right - window.Left, window.Bottom - window.Top,
            (int)Math.Round(shadow.Left * scale), (int)Math.Round(shadow.Top * scale),
            (int)Math.Round(shadow.Right * scale), (int)Math.Round(shadow.Bottom * scale));
        SetWindowPos(handle, TopmostWindow, x, y, 0, 0, NoSize | NoActivate | ShowWindow);
    }

    /// <summary>Window origin, in device pixels, that puts the visible card at <paramref name="position"/>.</summary>
    internal static (int X, int Y) Place(TouchdownCardPosition position, int left, int top, int right, int bottom,
        int width, int height, int shadowLeft, int shadowTop, int shadowRight, int shadowBottom)
    {
        var monitorHeight = bottom - top;
        var corner = (int)Math.Round(monitorHeight * CornerFraction);
        var center = left + (right - left - width) / 2;
        return position switch
        {
            TouchdownCardPosition.TopLeft => (left + corner - shadowLeft, top + corner - shadowTop),
            TouchdownCardPosition.TopCenter => (center, top + (int)Math.Round(monitorHeight * TopCenterFraction) - shadowTop),
            TouchdownCardPosition.BottomCenter => (center, bottom - (int)Math.Round(monitorHeight * BottomCenterFraction) - height + shadowBottom),
            _ => (right - corner - width + shadowRight, top + corner - shadowTop),
        };
    }

    private const int ExtendedStyle = -20;
    private const long TransparentStyle = 0x20, ToolWindowStyle = 0x80, NoActivateStyle = 0x08000000;
    private const int DefaultToPrimary = 1;
    private const uint NoSize = 0x1, NoActivate = 0x10, ShowWindow = 0x40;
    private static readonly IntPtr TopmostWindow = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
