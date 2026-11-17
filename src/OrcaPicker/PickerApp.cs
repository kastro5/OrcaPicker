using System.Diagnostics;

namespace OrcaPicker;

/// <summary>
/// Handles incoming items (links, files) one at a time: its own, plus any that later OrcaPicker
/// instances hand over while it's running (each gets its own pick).
/// </summary>
internal sealed class PickerApp : ApplicationContext
{
    private readonly Queue<string[]> _queue = new();
    private readonly object _gate = new();
    private readonly Control _ui = new();
    private bool _closing;
    private bool _processing;
    private Process? _lastLaunch;
    private DateTime _lastLaunchAt;
    private bool _repairPending;
    private System.Windows.Forms.Timer? _linger;

    public static int Run(string[] args)
    {
        using var instance = SingleInstance.AcquireOrForward(args);
        if (instance == null)
            return 0; // the running OrcaPicker has it

        var app = new PickerApp();
        app._queue.Enqueue(args);
        instance.StartServer(app.Accept);
        Application.Run(app);
        return 0;
    }

    private PickerApp()
    {
        _ui.CreateControl(); // a handle to marshal onto the UI thread
        _ = _ui.Handle;
        _ui.BeginInvoke(ProcessQueue);
    }

    /// <summary>Called on the pipe thread for items from other instances.</summary>
    private bool Accept(string[] args)
    {
        lock (_gate)
        {
            if (_closing)
                return false;
            _queue.Enqueue(args);
        }
        Log.Info("Queued item from another instance: " + string.Join(" | ", args));
        _ui.BeginInvoke(ProcessQueue);
        return true;
    }

    private void ProcessQueue()
    {
        if (_processing)
            return; // a modal dialog of the current item is open; the loop below will get to it
        _processing = true;
        try
        {
            while (true)
            {
                string[] args;
                lock (_gate)
                {
                    if (!_queue.TryDequeue(out args!))
                        break;
                }
                try
                {
                    ProcessOne(args);
                }
                catch (Exception ex)
                {
                    Log.Error("Processing item failed", ex);
                    Dialogs.Error("Something went wrong", ex.Message + "\n\nDetails are in " + AppPaths.LogFile);
                }
            }
        }
        finally
        {
            _processing = false;
        }

        TryExit();
    }

    /// <summary>
    /// Exit when the queue is empty. Linger a little (still accepting items) while a toast is showing,
    /// or while an Orca we just started hasn't yet re-registered orcaslicer:// (it does so at startup)
    /// and there is no watcher to take it back.
    /// </summary>
    private void TryExit()
    {
        if (_processing)
            return; // an item is being handled (the linger timer also ticks inside modal dialogs)
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                _ui.BeginInvoke(ProcessQueue);
                return;
            }
        }
        if (_repairPending && LaunchSettled())
        {
            _repairPending = false;
            try { Registration.CheckAndRepair(Settings.Load()); }
            catch (Exception ex) { Log.Warn($"Repair after launch: {ex.Message}"); }
        }
        if (Notifier.IsShowing || _repairPending)
        {
            if (_linger == null)
            {
                _linger = new System.Windows.Forms.Timer { Interval = 1000 };
                _linger.Tick += (_, _) => TryExit();
                _linger.Start();
            }
            return;
        }
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                _ui.BeginInvoke(ProcessQueue);
                return;
            }
            _closing = true;
        }
        _linger?.Dispose();
        _linger = null;
        Notifier.Hide();
        ExitThread();
    }

    /// <summary>The Orca we launched has its main window (so it has re-registered), exited, or it's been 90 s.</summary>
    private bool LaunchSettled()
    {
        var p = _lastLaunch;
        if (p == null || DateTime.UtcNow - _lastLaunchAt > TimeSpan.FromSeconds(90))
            return true;
        try
        {
            return p.HasExited || OrcaWindows.Find().Any(w => w.ProcessId == p.Id);
        }
        catch
        {
            return true;
        }
    }

    private void ProcessOne(string[] args)
    {
        var items = ParseArgs(args);
        foreach (var it in items)
            Log.Info($"Parsed: kind={it.Kind} display='{it.DisplayName}' forward='{it.Forward}'");

        var unknown = items.Where(i => i.Kind == InputKind.Unknown).ToList();
        items = items.Where(i => i.Kind != InputKind.Unknown).ToList();
        if (items.Count == 0)
        {
            Dialogs.Error("OrcaPicker didn't recognise this",
                "It isn't an existing file or a link with a model URL in it:\n\n" + string.Join("\n", args) +
                "\n\nThe raw text is in the log, so you can see what the site sent.",
                unknown.FirstOrDefault() is { } u ? u with { Kind = InputKind.Link } : null);
            return;
        }

        var settings = Settings.Load();
        var notices = new List<string>();
        try
        {
            foreach (var line in Registration.CheckAndRepair(settings))
                notices.Add(line + " — OrcaPicker took it back.");
        }
        catch (Exception ex)
        {
            Log.Error("Registration check failed", ex);
        }
        if (unknown.Count > 0)
            notices.Add($"Ignored {unknown.Count} argument(s) that weren't a file or a link (see log).");

        WaitForStartingOrca();
        var windows = OrcaWindows.Find();
        Log.Info($"Found {windows.Count} Orca window(s): {string.Join(" | ", windows.Select(w => $"0x{w.Handle:X} '{w.Title}'"))}");
        var forwards = items.Select(i => i.Forward).ToList();

        if (windows.Count == 0)
        {
            if (notices.Count > 0)
                Notifier.Show("OrcaPicker", string.Join("\n", notices));
            LaunchNew(settings, items, forwards);
            return;
        }

        if (OrcaLocator.ReadSingleInstancePreference() == true)
            notices.Add("OrcaSlicer's \"Allow only one OrcaSlicer instance\" preference is on, so \"New window\" " +
                        "will open in an existing window instead. Turn it off in Orca → Preferences.");

        using var picker = new PickerForm(items, windows, notices);
        if (picker.ShowDialog() != DialogResult.OK)
        {
            Log.Info("Picker cancelled.");
            return;
        }

        if (picker.SelectedWindow == null)
        {
            LaunchNew(settings, items, forwards);
            return;
        }

        var target = picker.SelectedWindow;
        Log.Info($"Sending to '{target.Title}' (0x{target.Handle:X})");
        var result = OrcaWindows.Send(target, forwards);
        if (result.Ok)
            return;

        const string newWindow = "Open in a new window";
        var clicked = Dialogs.Error("Couldn't send to that OrcaSlicer window", result.Message,
            items.FirstOrDefault(i => i.Kind == InputKind.Link), newWindow);
        if (clicked == newWindow)
            LaunchNew(settings, items, forwards);
    }

    /// <summary>
    /// Windows splits an unquoted path with spaces into several arguments. If the pieces don't make sense
    /// on their own but the joined text is an existing file, use that.
    /// </summary>
    internal static List<InputItem> ParseArgs(string[] args, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var items = args.Select(a => InputParser.Parse(a, fileExists)).ToList();
        if (args.Length > 1 && items.Any(i => i.Kind == InputKind.Unknown))
        {
            var joined = InputParser.Parse(string.Join(" ", args), fileExists);
            if (joined.Kind == InputKind.LocalFile)
                return [joined];
        }
        return items;
    }

    private void LaunchNew(Settings settings, IReadOnlyList<InputItem> items, List<string> forwards)
    {
        var exe = OrcaLocator.FindOrAsk(settings);
        if (exe == null)
        {
            Dialogs.Error("OrcaSlicer not found",
                "OrcaPicker couldn't find orca-slicer.exe. Set its path in OrcaPicker's settings (run OrcaPicker.exe with no arguments).",
                items.FirstOrDefault(i => i.Kind == InputKind.Link));
            return;
        }
        // Orca only downloads a link at startup when it's the only argument (GUI_App.cpp:960-963).
        if (forwards.Count > 1 && items.Any(i => i.Kind == InputKind.Link))
            Log.Warn("Several items including a link: a new Orca window only downloads a link when it's the only argument.");
        try
        {
            _lastLaunch?.Dispose();
            _lastLaunch = OrcaLocator.Launch(exe, forwards);
            _lastLaunchAt = DateTime.UtcNow;
            // Orca re-registers orcaslicer:// when it starts; take it back once it has (if no watcher does it).
            if (settings.Registered && !Watcher.IsRunning())
                _repairPending = true;
        }
        catch (Exception ex)
        {
            Log.Error("Launching Orca failed", ex);
            Dialogs.Error("Couldn't start OrcaSlicer", $"{exe}\n\n{ex.Message}", items.FirstOrDefault(i => i.Kind == InputKind.Link));
        }
    }

    /// <summary>
    /// If this OrcaPicker started an Orca a moment ago and it isn't ready yet, wait for its window
    /// rather than starting yet another Orca for the next queued item.
    /// </summary>
    private void WaitForStartingOrca()
    {
        if (LaunchSettled())
            return;
        Log.Info("Waiting for the OrcaSlicer that was just started.");
        var remaining = TimeSpan.FromSeconds(90) - (DateTime.UtcNow - _lastLaunchAt);
        using var wait = new WaitForm("Waiting for OrcaSlicer to finish starting, so you can choose it…", LaunchSettled, remaining);
        wait.ShowDialog();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _linger?.Dispose();
            _ui.Dispose();
            _lastLaunch?.Dispose();
            Notifier.Hide();
        }
        base.Dispose(disposing);
    }
}
