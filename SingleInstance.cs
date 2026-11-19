using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace HistoryBoard;

// One board per user and data folder. Two boards would write the same autosave and overwrite each
// other's unsaved lessons, so a second start (for example a double click on a .hboard file while the
// board is open) hands its file to the running board and exits.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private bool owned;

    private SingleInstance(string dataDirectory)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..24];
        mutex = new Mutex(false, @"Local\HistoryBoard-" + id);
        pipeName = "HistoryBoard-" + id;
    }

    // Returns the guard of the first board, or null when another board accepted the request.
    // Throws TimeoutException when a running board neither answers nor exits.
    public static SingleInstance? Acquire(string dataDirectory, string? file)
    {
        var instance = new SingleInstance(dataDirectory);
        if (instance.TryOwn(TimeSpan.Zero)) return instance;
        if (instance.TrySend(file ?? "", TimeSpan.FromSeconds(3))) { instance.Dispose(); return null; }
        // The running board may be closing: once it has exited this start becomes the board.
        if (instance.TryOwn(TimeSpan.FromSeconds(8))) return instance;
        instance.Dispose();
        throw new TimeoutException("Доска уже открыта, но не отвечает. Закройте её и попробуйте снова.");
    }

    private bool TryOwn(TimeSpan wait)
    {
        try { owned = mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { owned = true; } // the previous board crashed
        return owned;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    internal bool TrySend(string message, TimeSpan timeout)
    {
        try
        {
            // Windows lets the running board come to the front only with this start's permission.
            AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes); client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    // Each message is a full file path, or an empty string to bring the board to the front.
    public void Listen(Action<string> received)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop.Token);
                    using var memory = new MemoryStream();
                    var buffer = new byte[4096]; int count;
                    while ((count = await server.ReadAsync(buffer, stop.Token)) > 0)
                    {
                        if (memory.Length + count > 64 * 1024) break; // a path is never this long
                        memory.Write(buffer, 0, count);
                    }
                    received(Encoding.UTF8.GetString(memory.ToArray()).Trim());
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { CrashLog.Write("Канал запуска", ex); await Task.Delay(500); }
            }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        if (owned) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } owned = false; }
        mutex.Dispose();
    }
}
