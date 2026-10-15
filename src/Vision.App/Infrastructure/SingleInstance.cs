using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Vision.App.Infrastructure;

/// <summary>One Vision per user: a second launch (e.g. "Open with Vision" on a folder) hands its arguments
/// to the running instance over a named pipe and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The id of this app's mutex and pipe (Local\Vision-Windows-&lt;user&gt;).</summary>
    public const string AppId = "Vision-Windows";

    /// <summary>Legacy: the id DSH (Vision's former name) used. Only ever probed — an old DSH still running would
    /// keep writing to the data folder Vision brings over from it (see <see cref="IsRunning"/>).</summary>
    public const string LegacyAppId = "DSH-Windows";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance(string id)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName(id), out var created);
        IsFirst = created;
        _pipeName = $"{id}-{User}-pipe";
    }

    private static string User => Environment.UserName.Replace('\\', '_');

    private static string MutexName(string id) => $"Local\\{id}-{User}";

    /// <summary>Whether an app holding the mutex of <paramref name="id"/> is running for this user — without
    /// taking it.</summary>
    public static bool IsRunning(string id)
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName(id), out var existing)) return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // it exists; we just may not open it
        }
    }

    /// <summary>Send arguments to the running instance. True when it received them.</summary>
    public bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(3000);
            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(payload, 0, payload.Length);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Listen for forwarded arguments; <paramref name="onArgs"/> runs on a pool thread.</summary>
    public void Listen(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
