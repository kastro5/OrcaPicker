using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OrcaPicker;

/// <summary>
/// One picker at a time. The first OrcaPicker that gets an item owns a named mutex and runs a named-pipe
/// server. Later OrcaPickers (a second link clicked quickly) send their arguments to it and exit. The first
/// one queues them and shows one pick per item. The server answers "OK" only once the item is queued,
/// so if the first instance is just shutting down, the second one notices and takes over instead.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\OrcaPicker.Picker";
    private static string PipeName
    {
        get
        {
            Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint session);
            return $"OrcaPicker.Picker.{Environment.UserName}.{session}";
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _server;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Either becomes the primary instance (returns it), or hands <paramref name="args"/> to the primary
    /// one (returns null). Retries for a while if the primary is just exiting.
    /// </summary>
    public static SingleInstance? AcquireOrForward(string[] args)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var mutex = new Mutex(false, MutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (owned)
                return new SingleInstance(mutex);
            mutex.Dispose();

            if (TrySend(args))
                return null;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Another OrcaPicker is running but not accepting items.");
            Thread.Sleep(250);
        }
    }

    private static bool TrySend(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(1000);
            // Let the primary instance put its picker in front of the browser.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
                Native.AllowSetForegroundWindow(serverPid);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(pipe, Encoding.UTF8);
            writer.WriteLine(JsonSerializer.Serialize(args));
            string? reply = reader.ReadLine();
            Log.Info($"Handed item to running OrcaPicker (pid {serverPid}): {reply}");
            return reply == "OK";
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't hand item to running OrcaPicker: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Start accepting items from other instances. <paramref name="accept"/> runs on a background thread.
    /// It returns false once the primary instance has started closing.
    /// </summary>
    public void StartServer(Func<string[], bool> accept)
    {
        _server = new Thread(() => ServerLoop(accept)) { IsBackground = true, Name = "OrcaPicker pipe" };
        _server.Start();
    }

    private void ServerLoop(Func<string[], bool> accept)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                var reader = new StreamReader(pipe, Encoding.UTF8);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                string? line = reader.ReadLine();
                string[] args = line == null ? [] : JsonSerializer.Deserialize<string[]>(line) ?? [];
                bool ok = args.Length > 0 && accept(args);
                writer.WriteLine(ok ? "OK" : "NO");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Pipe server: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server?.Join(2000);
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
