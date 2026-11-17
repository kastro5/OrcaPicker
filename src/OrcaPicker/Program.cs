using System.Text.Json;

namespace OrcaPicker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Every raw argument goes to the log first, so you can see exactly what each site sends.
        Log.Info($"Start v{typeof(Program).Assembly.GetName().Version} args={JsonSerializer.Serialize(args)}");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

        try
        {
            ApplicationConfiguration.Initialize();

            bool quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
            string first = args.Length > 0 ? args[0] : "";
            switch (first.ToLowerInvariant())
            {
                case "":
                case "--settings":
                    Application.Run(new SettingsForm());
                    return 0;

                case "--register":
                {
                    var report = Registration.Register(Settings.Load());
                    if (!quiet)
                        Dialogs.Info("Registered", string.Join("\n", report) +
                            "\n\nTip: in OrcaSlicer → Preferences, untick \"Associate prusaslicer://\" and \"Associate bambustudio://\". " +
                            "OrcaSlicer takes orcaslicer:// back at every start; the watcher puts OrcaPicker back.");
                    return 0;
                }

                case "--unregister":
                {
                    var report = Registration.UnregisterAll(Settings.Load());
                    if (!quiet)
                        Dialogs.Info("Unregistered", report.Count == 0 ? "Nothing was registered." : string.Join("\n", report));
                    return 0;
                }

                case "--watch":
                    return Watcher.Run();

                case "--diag":
                {
                    string report = Diagnostics();
                    Log.Info("Diagnostics:\n" + report);
                    if (!quiet)
                        Dialogs.Info("OrcaPicker diagnostics", report);
                    return 0;
                }

                default:
                    return PickerApp.Run(args.Where(a => !a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)).ToArray());
            }
        }
        catch (Exception ex)
        {
            Fatal(ex);
            return 1;
        }
    }

    /// <summary>What OrcaPicker sees right now. Read-only.</summary>
    private static string Diagnostics()
    {
        var settings = Settings.Load();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"OrcaPicker: {AppPaths.ExePath}");
        sb.AppendLine($"orca-slicer.exe: {OrcaLocator.Find(settings) ?? "NOT FOUND"}");
        sb.AppendLine($"Orca 'Allow only one instance': {OrcaLocator.ReadSingleInstancePreference()?.ToString() ?? "unknown"}");
        sb.AppendLine($"Registered: {settings.Registered}, watcher running: {Watcher.IsRunning()}, at logon: {Registration.IsWatcherInstalled()}");
        foreach (var scheme in InputParser.RegisteredSchemes)
            sb.AppendLine($"  {scheme}:// → {Registration.CurrentHandler(scheme)}{(settings.IsSchemeEnabled(scheme) ? "" : " (unticked)")}");
        var windows = OrcaWindows.Find();
        sb.AppendLine($"Orca windows ({windows.Count}), most recent first:");
        foreach (var w in windows)
            sb.AppendLine($"  0x{w.Handle:X} pid {w.ProcessId}{(w.Minimized ? " minimized" : "")} '{w.Title}'");
        return sb.ToString();
    }

    private static void Fatal(Exception? ex)
    {
        Log.Error("Unhandled exception", ex);
        try
        {
            MessageBox.Show(
                "Sorry, OrcaPicker hit an unexpected problem and couldn't finish.\n\n" +
                (ex?.Message ?? "Unknown error") + "\n\nDetails are in:\n" + AppPaths.LogFile,
                "OrcaPicker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            // Nothing else we can do.
        }
    }
}
