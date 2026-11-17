using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OrcaPicker;

/// <summary>
/// "OrcaPicker --watch": a windowless process started at logon (HKCU Run key). It sleeps in
/// RegNotifyChangeKeyValue on HKCU\Software\Classes\&lt;scheme&gt; and puts OrcaPicker back as soon as another
/// program (OrcaSlicer does this at every start, see docs/orca-ipc.md §6) overwrites a scheme.
/// It does nothing else and never touches the network.
/// </summary>
internal static class Watcher
{
    private const string MutexName = @"Local\OrcaPicker.Watcher";
    private const string WakeEventName = @"Local\OrcaPicker.WatcherWake";

    private const int REG_NOTIFY_CHANGE_NAME = 0x1;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, int dwNotifyFilter,
        SafeWaitHandle hEvent, bool fAsynchronous);

    /// <summary>Ask a running watcher to re-read settings (and exit if it's no longer wanted).</summary>
    public static void Wake()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(WakeEventName, out var ev))
                using (ev) ev.Set();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not wake watcher: {ex.Message}");
        }
    }

    public static bool IsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var m))
            {
                m.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        return false;
    }

    public static void StartIfNotRunning()
    {
        if (IsRunning())
        {
            Wake();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExePath, "--watch") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start watcher", ex);
        }
    }

    public static int Run()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log.Info("Watcher already running; exiting.");
            return 0;
        }
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        Log.Info("Watcher started.");

        var repairs = new Dictionary<string, Queue<DateTime>>();
        var pausedUntil = new Dictionary<string, DateTime>();

        try
        {
            while (true)
            {
                var settings = Settings.Load();
                if (!settings.Registered || !settings.Watcher)
                {
                    Log.Info("Watcher no longer wanted; exiting.");
                    return 0;
                }

                // Repair anything that was changed, with a guard against fighting another program in a loop.
                var schemes = settings.EnabledSchemes
                    .Where(s => !pausedUntil.TryGetValue(s, out var until) || DateTime.UtcNow >= until)
                    .ToList();
                foreach (var scheme in schemes)
                {
                    if (Registration.IsOurs(scheme))
                        continue;
                    var q = repairs.TryGetValue(scheme, out var existing) ? existing : repairs[scheme] = new Queue<DateTime>();
                    while (q.Count > 0 && DateTime.UtcNow - q.Peek() > TimeSpan.FromMinutes(1))
                        q.Dequeue();
                    if (q.Count >= 5)
                    {
                        Log.Warn($"Watcher: {scheme}:// was overwritten 5 times in a minute; pausing it for 10 minutes.");
                        pausedUntil[scheme] = DateTime.UtcNow.AddMinutes(10);
                        continue;
                    }
                    q.Enqueue(DateTime.UtcNow);
                    var one = new Settings { Registered = true, Schemes = new() { [scheme] = true } };
                    foreach (var line in Registration.CheckAndRepair(one))
                        Log.Info("Watcher: " + line);
                }

                // Arm one notification per scheme key, then sleep until something changes.
                var keys = new List<RegistryKey>();
                var events = new List<WaitHandle>();
                try
                {
                    foreach (var scheme in schemes)
                    {
                        var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}");
                        if (key == null)
                            continue;
                        var ev = new AutoResetEvent(false);
                        int rc = RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                            ev.SafeWaitHandle, true);
                        if (rc != 0)
                        {
                            Log.Warn($"RegNotifyChangeKeyValue({scheme}) failed: {rc}");
                            key.Dispose();
                            ev.Dispose();
                            continue;
                        }
                        keys.Add(key);
                        events.Add(ev);
                    }
                    events.Add(wake);
                    // Heartbeat every 10 minutes (also picks up keys that were deleted and recreated).
                    WaitHandle.WaitAny(events.ToArray(), TimeSpan.FromMinutes(10));
                }
                finally
                {
                    foreach (var k in keys) k.Dispose();
                    foreach (var e in events) if (e != wake) e.Dispose();
                }

                // Let the other program finish writing (Orca writes the root key, then the command).
                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Watcher crashed", ex);
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
