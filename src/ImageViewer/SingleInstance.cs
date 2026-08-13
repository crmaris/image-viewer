using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace ImageViewer;

/// <summary>
/// Routes a second launch into the already-running viewer instead of starting a new process.
/// </summary>
/// <remarks>
/// This is the single biggest perceived-speed win in the whole application. Double-clicking one
/// image after another normally pays a full process start every time; here the second launch does
/// nothing but connect to a pipe, hand over the path and exit, while the window that appears is one
/// that was already running. Critically, <see cref="TryHandOff"/> is called from Main <em>before</em>
/// any WPF type is referenced, so the short-lived process never pays to initialise a UI framework
/// it is about to throw away.
/// </remarks>
public static class SingleInstance
{
    // Session-scoped so two users logged into the same machine get independent viewers.
    private static readonly string Scope = $"ImageViewer.{Process.GetCurrentProcess().SessionId:X}";
    private static readonly string MutexName = $@"Local\{Scope}.mutex";
    private static readonly string PipeName = $"{Scope}.pipe";

    private static Mutex? _ownershipMutex;

    /// <summary>Raised on a background thread when another instance hands over paths.</summary>
    public static event Action<string[]>? PathsReceived;

    /// <summary>
    /// Tries to give <paramref name="paths"/> to a running instance.
    /// </summary>
    /// <returns>
    /// True if a running instance accepted them and this process should exit immediately;
    /// false if this process is the primary instance and should show the UI.
    /// </returns>
    public static bool TryHandOff(string[] paths)
    {
        try
        {
            _ownershipMutex = new Mutex(initiallyOwned: true, MutexName, out var isPrimary);

            if (isPrimary)
            {
                // We own the name, so we are the real instance. Keep the mutex alive for the
                // process lifetime - releasing it would let a later launch believe it is primary.
                return false;
            }
        }
        catch
        {
            // If the mutex cannot be created at all, degrade to just running normally rather than
            // refusing to start.
            return false;
        }

        // Another instance owns the name. Hand the paths over and let it surface itself.
        // Nothing to say means "just bring the existing window forward".
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.None);

            // Short timeout: if the primary is wedged or mid-shutdown we would rather start our own
            // window than leave the user staring at nothing after a double-click. A healthy primary
            // answers in single-digit milliseconds, so this only ever costs anything when the mutex
            // owner has died without releasing it - a case measured at over a second at 800 ms.
            client.Connect(timeout: 400);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', paths));
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch
        {
            // The owner is gone or unreachable (it may have died without releasing the mutex).
            // Carry on as a normal instance.
            return false;
        }
    }

    /// <summary>
    /// Starts listening for handoffs from later launches. Only the primary instance calls this.
    /// </summary>
    public static void StartServer(CancellationToken ct)
    {
        var thread = new Thread(() => ServerLoop(ct))
        {
            IsBackground = true,
            Name = "SingleInstance pipe",
        };
        thread.Start();
    }

    private static void ServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();

                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = reader.ReadToEnd();

                var paths = text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                PathsReceived?.Invoke(paths);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A malformed or aborted handoff must never take down the viewer. Pause briefly so
                // a persistently failing pipe cannot spin the CPU, then rebuild the server.
                try { Task.Delay(200, ct).GetAwaiter().GetResult(); }
                catch { return; }
            }
        }
    }
}
