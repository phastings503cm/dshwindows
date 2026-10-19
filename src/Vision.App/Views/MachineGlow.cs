using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Vision.App.Model;
using Vision.Core;
using Vision.Windows;

namespace Vision.App.Views;

// MARK: - "Machine in use by Vision AI"
//
// While a chat uses the computer — looking at it, clicking, typing — the edges of every monitor glow
// and a label at the bottom says which chat is doing what, and how to stop it. The glow is a set of
// thin, layered, click-through, topmost windows along each monitor's edges plus one for the label:
// they never take a click or the focus, and Windows leaves them out of screenshots (WDA_EXCLUDE-
// FROMCAPTURE), so the model never sees them. Over Remote Desktop the person watching is on the other
// end of a capture, so there the glow stays visible and steps aside for each screenshot instead.
// Ctrl+Alt+. stops every chat using the machine, from any app.

/// <summary>The glow's look, shared by the overlay and the self-test's pictures of it.</summary>
internal static class GlowLook
{
    public static readonly Color Green = Color.FromRgb(0x33, 0xF2, 0x8C);
    public static readonly Color Cyan = Color.FromRgb(0x26, 0xBF, 0xF2);
    /// <summary>How far the glow reaches in from the edge (device-independent pixels).</summary>
    public const double Band = 16;
    /// <summary>The label's gap above the bottom of the work area (above the taskbar).</summary>
    public const double LabelGap = 18;

    public enum Side { Top, Bottom, Left, Right }

    /// <summary>One edge: green-cyan-green along it, fading from the screen edge inward.</summary>
    public static Border Edge(Side side, bool animate)
    {
        var horizontal = side is Side.Top or Side.Bottom;
        var along = new LinearGradientBrush
        {
            StartPoint = horizontal ? new Point(0, 0.5) : new Point(0.5, 0),
            EndPoint = horizontal ? new Point(1, 0.5) : new Point(0.5, 1),
        };
        along.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, Green.R, Green.G, Green.B), 0));
        along.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, Cyan.R, Cyan.G, Cyan.B), 0.5));
        along.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, Green.R, Green.G, Green.B), 1));
        var (start, end) = side switch
        {
            Side.Top => (new Point(0.5, 0), new Point(0.5, 1)),
            Side.Bottom => (new Point(0.5, 1), new Point(0.5, 0)),
            Side.Left => (new Point(0, 0.5), new Point(1, 0.5)),
            _ => (new Point(1, 0.5), new Point(0, 0.5)),
        };
        var fade = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0, 0, 0), 0.2));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0, 0, 0), 0.6));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        var edge = new Border { Background = along, OpacityMask = fade, IsHitTestVisible = false, SnapsToDevicePixels = true };
        if (animate) Pulse(edge);
        return edge;
    }

    /// <summary>A slow breath (1.6 s each way, as on the Mac), at a low frame rate: alive, not loud.</summary>
    public static void Pulse(UIElement element)
    {
        var breath = new DoubleAnimation(1.0, 0.55, TimeSpan.FromSeconds(1.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(breath, 30);
        element.BeginAnimation(UIElement.OpacityProperty, breath);
    }

    /// <summary>The label: "● Machine in use by Vision AI · Ctrl+Alt+. stops" over the line naming the chat.</summary>
    public static Border Label(GlowText text)
    {
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Green),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Green, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 },
        };
        var headline = new TextBlock { Text = text.Headline, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        var hint = new TextBlock
        {
            Text = "  ·  " + text.Hint,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(dot);
        top.Children.Add(headline);
        top.Children.Add(hint);
        var detail = new TextBlock
        {
            Text = text.Detail,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(16, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 700,
        };
        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(detail);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC4, 0x0B, 0x0F, 0x0D)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, Green.R, Green.G, Green.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 7, 16, 8),
            Child = stack,
            IsHitTestVisible = false,
            // Room for the soft shadow inside the label's own window.
            Margin = new Thickness(10),
            Effect = new DropShadowEffect { Color = Green, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.45 },
        };
    }

    /// <summary>The four edge strips of a monitor, in physical pixels: top and bottom span the width,
    /// left and right fit between them.</summary>
    public static IReadOnlyList<(Side Side, ScreenRect Rect)> EdgeRects(ScreenRect monitor, double scale)
    {
        var band = Math.Max(4, (int)Math.Round(Band * scale));
        var inner = Math.Max(0, monitor.Height - 2 * band);
        return
        [
            (Side.Top, new ScreenRect(monitor.X, monitor.Y, monitor.Width, band)),
            (Side.Bottom, new ScreenRect(monitor.X, monitor.Bottom - band, monitor.Width, band)),
            (Side.Left, new ScreenRect(monitor.X, monitor.Y + band, band, inner)),
            (Side.Right, new ScreenRect(monitor.Right - band, monitor.Y + band, band, inner)),
        ];
    }

    /// <summary>Where a label of this size (physical pixels) sits: centred, just above the work area's bottom.</summary>
    public static (int X, int Y) LabelSpot(MonitorInfo monitor, int width, int height) =>
        (monitor.Bounds.X + (monitor.Bounds.Width - width) / 2,
         monitor.WorkArea.Bottom - height - (int)Math.Round((LabelGap - 10) * monitor.Scale));

    /// <summary>A picture of the glow on a made-up desktop, for the self-test (and the README).</summary>
    public static FrameworkElement Preview(GlowText text, double width = 1280, double height = 720)
    {
        var desktop = new Grid { Width = width, Height = height, ClipToBounds = true };
        var wallpaper = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x4A), Color.FromRgb(0x3A, 0x5F, 0x8F), 35);
        desktop.Children.Add(new Border { Background = wallpaper });
        // An app the chat is driving, and the taskbar.
        var app = new Border
        {
            Width = width * 0.55,
            Height = height * 0.55,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF5)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(width * 0.12, height * 0.12, 0, 0),
        };
        var appStack = new StackPanel();
        appStack.Children.Add(new Border
        {
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xE6)),
            Child = new TextBlock { Text = "Inventory — Stock counts", Margin = new Thickness(12, 7, 0, 0), FontSize = 12, Foreground = Brushes.Black },
        });
        appStack.Children.Add(new TextBlock { Text = "SKU-1042   Bolts M6     118", Margin = new Thickness(16, 18, 0, 0), FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Black });
        appStack.Children.Add(new TextBlock { Text = "SKU-1043   Washers M6    64", Margin = new Thickness(16, 6, 0, 0), FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Black });
        appStack.Children.Add(new Border
        {
            Width = 96,
            Height = 28,
            Margin = new Thickness(16, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xD0)),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock { Text = "Save", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        app.Child = appStack;
        desktop.Children.Add(app);
        const double taskbar = 40;
        desktop.Children.Add(new Border
        {
            Height = taskbar,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x20, 0x20, 0x24)),
        });
        // The glow over it all, as the overlay draws it.
        foreach (var side in Enum.GetValues<Side>())
        {
            var edge = Edge(side, animate: false);
            switch (side)
            {
                case Side.Top: edge.Height = Band; edge.VerticalAlignment = VerticalAlignment.Top; break;
                case Side.Bottom: edge.Height = Band; edge.VerticalAlignment = VerticalAlignment.Bottom; break;
                case Side.Left: edge.Width = Band; edge.HorizontalAlignment = HorizontalAlignment.Left; edge.Margin = new Thickness(0, Band, 0, Band); break;
                default: edge.Width = Band; edge.HorizontalAlignment = HorizontalAlignment.Right; edge.Margin = new Thickness(0, Band, 0, Band); break;
            }
            desktop.Children.Add(edge);
        }
        var label = Label(text);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.Margin = new Thickness(0, 0, 0, taskbar + LabelGap - 10);
        desktop.Children.Add(label);
        return desktop;
    }
}

/// <summary>One of the glow's windows: layered, click-through, never activated, topmost.</summary>
internal sealed class GlowWindow : Window
{
    private const int WmDpiChanged = 0x02E0;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private readonly bool _exclude;

    public GlowWindow(FrameworkElement content, bool excludeFromCapture, bool sizeToContent)
    {
        _exclude = excludeFromCapture;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Vision machine glow";
        Content = content;
        if (sizeToContent) SizeToContent = SizeToContent.WidthAndHeight;
        else
        {
            Width = 16;
            Height = 16;
        }
        Left = -32000;
        Top = -32000;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            OverlayWindows.MakeClickThrough(Handle);
            Excluded = OverlayWindows.ExcludeFromCapture(Handle, _exclude);
            HwndSource.FromHwnd(Handle)?.AddHook(Hook);
        };
    }

    public IntPtr Handle { get; private set; }
    /// <summary>Windows leaves this window out of captures.</summary>
    public bool Excluded { get; private set; }
    /// <summary>For an edge: the exact rectangle it keeps (physical pixels).</summary>
    public ScreenRect? Pin { get; set; }
    /// <summary>For a label: the monitor it is centred on.</summary>
    public MonitorInfo? Monitor { get; set; }

    /// <summary>Create the window and put it in place before it is first drawn.</summary>
    public void ShowInPlace()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (Pin is { } pin) OverlayWindows.Place(Handle, pin);
        Show();
        Reposition();
        if (Monitor is not null) SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Back to its pinned rectangle, or centred above the taskbar for a label.</summary>
    public void Reposition()
    {
        if (Handle == IntPtr.Zero) return;
        if (Pin is { } pin)
        {
            OverlayWindows.Place(Handle, pin);
        }
        else if (Monitor is { } monitor)
        {
            var rect = OverlayWindows.Rect(Handle);
            var (x, y) = GlowLook.LabelSpot(monitor, rect.Width, rect.Height);
            OverlayWindows.Move(Handle, x, y);
        }
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmMouseActivate:
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE
            case WmNcHitTest:
                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT
            case WmDpiChanged:
                // WPF resizes to keep its size in device-independent pixels; the glow keeps physical ones.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, Reposition);
                break;
        }
        return IntPtr.Zero;
    }
}

/// <summary>Shows the glow while any chat uses the machine, keeps its label current, owns the stop
/// key, and watches for the person taking the mouse.</summary>
public sealed class MachineGlowController
{
    private const int StopKeyId = 0x5649; // "VI"

    private readonly AppModel _model;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly List<GlowWindow> _edges = [];
    private readonly List<GlowWindow> _labels = [];
    private HwndSource? _keySink;
    private bool _keyRegistered;
    private GlowText? _shown;
    private GlowText? _preview;

    public static MachineGlowController? Current { get; private set; }

    /// <summary>Start following the machine's use (once, at startup).</summary>
    public static MachineGlowController Install(AppModel model, Dispatcher dispatcher) => Current ??= new MachineGlowController(model, dispatcher);

    private MachineGlowController(AppModel model, Dispatcher dispatcher)
    {
        _model = model;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        MachineControl.Activity.Started += () => _dispatcher.BeginInvoke(Refresh);
        SystemEvents.DisplaySettingsChanged += (_, _) => _dispatcher.BeginInvoke(Rebuild);
        // Connecting or disconnecting Remote Desktop changes whether the glow may be left out of captures.
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.RemoteDisconnect
                or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.ConsoleDisconnect)
            {
                _dispatcher.BeginInvoke(Rebuild);
            }
        };
    }

    public bool Visible => _edges.Count > 0;
    /// <summary>Every glow window is left out of captures by Windows.</summary>
    public bool ExcludedFromCapture => Visible && _edges.Concat(_labels).All(w => w.Excluded);
    /// <summary>Ctrl+Alt+. is registered (another app may hold it).</summary>
    public bool StopKeyRegistered => _keyRegistered;
    /// <summary>The glow's windows (self-test checks).</summary>
    internal IReadOnlyList<GlowWindow> Windows => [.. _edges, .. _labels];
    internal GlowText? Shown => _shown;

    /// <summary>Self-test: show the glow with this label whatever the chats are doing (null = follow them again).</summary>
    public void Preview(GlowText? text)
    {
        _preview = text;
        Refresh();
    }

    public void Refresh()
    {
        GlowText? text;
        try
        {
            text = _preview ?? MachineText.Glow(MachineControl.Activity.Sessions(), MachineControl.Arbiter.Snapshot());
        }
        catch (Exception)
        {
            text = null;
        }
        if (text is null)
        {
            Hide();
            return;
        }
        if (!Visible) Show();
        if (!_keyRegistered) text = text with { Hint = MachineText.StopHintInApp };
        if (text != _shown) SetLabel(text);
    }

    private void Show()
    {
        var exclude = !Monitors.RemoteSession;
        foreach (var monitor in Monitors.All())
        {
            foreach (var (side, rect) in GlowLook.EdgeRects(monitor.Bounds, monitor.Scale))
            {
                var edge = new GlowWindow(GlowLook.Edge(side, animate: true), exclude, sizeToContent: false) { Pin = rect };
                edge.ShowInPlace();
                _edges.Add(edge);
            }
            var label = new GlowWindow(new ContentControl(), exclude, sizeToContent: true) { Monitor = monitor };
            label.ShowInPlace();
            _labels.Add(label);
        }
        _shown = null;
        // Where Windows can't leave the glow out of a capture, it steps aside for each one.
        CaptureShield.Hide = ExcludedFromCapture ? null : StepAside;
        RegisterStopKey();
        PersonInput.Shared.Start();
        _timer.Start();
    }

    private void Hide()
    {
        if (!Visible && _labels.Count == 0) return;
        _timer.Stop();
        CaptureShield.Hide = null;
        foreach (var window in _edges.Concat(_labels))
        {
            try { window.Close(); } catch (InvalidOperationException) { }
        }
        _edges.Clear();
        _labels.Clear();
        _shown = null;
        UnregisterStopKey();
        PersonInput.Shared.Stop();
    }

    /// <summary>Monitors changed (or Remote Desktop came or went): draw the glow again.</summary>
    private void Rebuild()
    {
        if (!Visible) return;
        Hide();
        Refresh();
    }

    private void SetLabel(GlowText text)
    {
        _shown = text;
        foreach (var label in _labels)
        {
            if (label.Content is ContentControl host) host.Content = GlowLook.Label(text);
        }
    }

    /// <summary>Hide every glow window for one capture (called on the capturing thread).</summary>
    private IDisposable? StepAside()
    {
        var windows = _edges.Concat(_labels).Select(w => w.Handle).Where(h => h != IntPtr.Zero).ToList();
        if (windows.Count == 0) return null;
        void HideAll()
        {
            foreach (var handle in windows) OverlayWindows.SetVisible(handle, false);
        }
        if (_dispatcher.CheckAccess()) HideAll();
        else if (_dispatcher.InvokeAsync(HideAll, DispatcherPriority.Send).Wait(TimeSpan.FromMilliseconds(500)) != DispatcherOperationStatus.Completed)
            return null;
        // SW_HIDE through ShowWindowAsync lands on the window's thread; give it and the compositor a moment.
        Thread.Sleep(30);
        OverlayWindows.Flush();
        return new Restore(windows);
    }

    private sealed class Restore(List<IntPtr> windows) : IDisposable
    {
        public void Dispose()
        {
            foreach (var handle in windows) OverlayWindows.SetVisible(handle, true);
        }
    }

    // MARK: The stop key

    private void RegisterStopKey()
    {
        if (_keyRegistered) return;
        _keySink ??= CreateKeySink();
        _keyRegistered = _keySink is not null && OverlayWindows.RegisterStopKey(_keySink.Handle, StopKeyId);
    }

    private void UnregisterStopKey()
    {
        if (!_keyRegistered || _keySink is null) return;
        OverlayWindows.UnregisterStopKey(_keySink.Handle, StopKeyId);
        _keyRegistered = false;
    }

    /// <summary>A message-only window that hears WM_HOTKEY.</summary>
    private HwndSource? CreateKeySink()
    {
        try
        {
            var sink = new HwndSource(new HwndSourceParameters("Vision stop key") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            sink.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool handled) =>
            {
                if (message == OverlayWindows.HotKeyMessage && wParam.ToInt32() == StopKeyId)
                {
                    handled = true;
                    StopFromKey();
                }
                return IntPtr.Zero;
            });
            return sink;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Ctrl+Alt+. pressed anywhere: every chat using the machine stops, and the glow goes.</summary>
    public void StopFromKey()
    {
        _model.Host.StopMachineChats();
        _preview = null;
        Hide();
    }
}
