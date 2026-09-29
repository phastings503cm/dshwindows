using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dsh.Core;

namespace Dsh.Windows.Tests;

/// <summary>Tests that touch the real desktop: windows, focus, the mouse. They run one at a time so
/// a click or a focus change can't land in another test's window.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopCollection
{
    public const string Name = "Desktop";
}

/// <summary>Runs a tool directly (no engine) with full access in a scratch folder.</summary>
internal static class Tools
{
    public static ToolContext Context(string workspace) => new()
    {
        Workspace = workspace,
        Policy = new PermissionPolicy(PermissionPreset.FullAccess, workspace),
        Client = new NoClient(),
        Registry = new ToolRegistry([]),
    };

    public static Task<ToolResult> Run(IToolExecutor tool, string arguments, string? workspace = null) =>
        tool.ExecuteAsync(arguments, Context(workspace ?? Path.GetTempPath()), CancellationToken.None);

    public static async Task<string> Output(IToolExecutor tool, string arguments) => (await Run(tool, arguments)).Output;

    /// <summary>The machine tool with this name.</summary>
    public static IToolExecutor Named(string name) => MachineTools.All().Single(t => t.Name == name);

    /// <summary>A JSON string literal.</summary>
    public static string Q(string value) => JsonArgs.Quote(value);
}

/// <summary>The machine tools never talk to the model.</summary>
internal sealed class NoClient : ILlmClient
{
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
                                                              [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>A unique scratch folder, deleted when disposed.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dsh-machine-{Guid.NewGuid():N}");

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

/// <summary>Encoded test images with a gradient, so they compress like real pictures rather than
/// to nothing.</summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height) => Encode(new PngBitmapEncoder(), width, height);

    public static byte[] Jpeg(int width, int height) => Encode(new JpegBitmapEncoder { QualityLevel = 85 }, width, height);

    public static byte[] Bmp(int width, int height) => Encode(new BmpBitmapEncoder(), width, height);

    private static byte[] Encode(BitmapEncoder encoder, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[i + 2] = (byte)((x ^ y) & 0xFF);
                pixels[i + 3] = 0xFF;
            }
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>A real WPF window on its own STA thread, topmost, for tests that need something on
/// screen: a button that counts clicks and a text box. <see cref="Animate"/> makes it repaint
/// continuously (a "live game").</summary>
internal sealed class TestWindow : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private Window? _window;
    private Button? _button;
    private TextBox? _box;
    private Border? _pad;
    private readonly List<(string Kind, int X, int Y)> _padEvents = [];
    private int _clicks;

    public string Title { get; }
    public IntPtr Handle { get; private set; }
    public int Clicks => Volatile.Read(ref _clicks);

    public TestWindow(bool withControls = true, bool animate = false)
    {
        Title = $"DSH machine test {Guid.NewGuid().ToString("N")[..10]}";
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                var panel = new StackPanel { Background = Brushes.White };
                if (withControls)
                {
                    _button = new Button { Content = "Press me", Width = 160, Height = 60, Margin = new Thickness(20) };
                    _button.Click += (_, _) => Interlocked.Increment(ref _clicks);
                    System.Windows.Automation.AutomationProperties.SetAutomationId(_button, "pressMe");
                    panel.Children.Add(_button);
                    _box = new TextBox { Text = "hello tree", Width = 200 };
                    panel.Children.Add(_box);
                    // Records where the left button goes down and comes up, for the drag test.
                    _pad = new Border { Width = 240, Height = 60, Margin = new Thickness(0, 8, 0, 0), Background = Brushes.LightGray };
                    _pad.MouseLeftButtonDown += (_, e) =>
                    {
                        Record("down", e);
                        _pad.CaptureMouse();
                    };
                    _pad.MouseLeftButtonUp += (_, e) =>
                    {
                        Record("up", e);
                        _pad.ReleaseMouseCapture();
                    };
                    panel.Children.Add(_pad);
                }
                else
                {
                    panel.Children.Add(new TextBlock { Text = "still life", FontSize = 32, Margin = new Thickness(20) });
                }
                _window = new Window
                {
                    Title = Title,
                    Content = panel,
                    Width = 420,
                    Height = 260,
                    Left = 60,
                    Top = 60,
                    Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                if (animate)
                {
                    var tick = 0;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                    timer.Tick += (_, _) =>
                    {
                        tick++;
                        panel.Background = new SolidColorBrush(Color.FromRgb((byte)(tick * 40), (byte)(255 - tick * 25), (byte)(tick * 70)));
                    };
                    timer.Start();
                }
                _window.Show();
                Handle = new WindowInteropHelper(_window).Handle;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }
            if (failure is null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "test window",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("the test window never appeared");
        if (failure is not null) throw failure;
        Thread.Sleep(400); // first render
    }

    public T Invoke<T>(Func<T> read) => _dispatcher!.Invoke(read);

    private void Record(string kind, System.Windows.Input.MouseButtonEventArgs e)
    {
        var point = _pad!.PointToScreen(e.GetPosition(_pad));
        lock (_padEvents) _padEvents.Add((kind, (int)Math.Round(point.X), (int)Math.Round(point.Y)));
    }

    /// <summary>Button downs and ups seen on the pad, in screen pixels.</summary>
    public IReadOnlyList<(string Kind, int X, int Y)> PadEvents()
    {
        lock (_padEvents) return [.. _padEvents];
    }

    /// <summary>A point on the pad (fractions of its size) in screen pixels.</summary>
    public (int X, int Y) PadPoint(double fx, double fy) => Invoke(() =>
    {
        var point = _pad!.PointToScreen(new Point(_pad.ActualWidth * fx, _pad.ActualHeight * fy));
        return ((int)Math.Round(point.X), (int)Math.Round(point.Y));
    });

    /// <summary>Give the text box keyboard focus (within the window).</summary>
    public void FocusBox() => Invoke(() => _box!.Focus());

    /// <summary>What the text box holds now.</summary>
    public string BoxText() => Invoke(() => _box?.Text ?? "");

    /// <summary>The button's centre in screen pixels.</summary>
    public (int X, int Y) ButtonCenter() => Invoke(() =>
    {
        var point = _button!.PointToScreen(new Point(_button.ActualWidth / 2, _button.ActualHeight / 2));
        return ((int)Math.Round(point.X), (int)Math.Round(point.Y));
    });

    public void Dispose()
    {
        try
        {
            _dispatcher?.Invoke(() => _window?.Close());
            _dispatcher?.InvokeShutdown();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            // Already shut down.
        }
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
