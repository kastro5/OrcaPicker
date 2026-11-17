using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace OrcaPicker;

/// <summary>Finds orca-slicer.exe, launches new Orca windows and reads Orca's single-instance preference.</summary>
internal static class OrcaLocator
{
    /// <summary>
    /// Search order: settings override → a running orca-slicer.exe → Uninstall entries / App Paths →
    /// %ProgramFiles%\OrcaSlicer. Returns null if nothing is found.
    /// </summary>
    public static string? Find(Settings settings)
    {
        foreach (var candidate in Candidates(settings))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static IEnumerable<string?> Candidates(Settings settings)
    {
        yield return settings.OrcaPath;

        foreach (var p in SafeProcesses())
            yield return p;

        yield return ReadDefault(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + OrcaWindows.ExeName);
        yield return ReadDefault(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + OrcaWindows.ExeName);

        foreach (var p in FromUninstallEntries())
            yield return p;

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OrcaSlicer", OrcaWindows.ExeName);
    }

    private static IEnumerable<string?> SafeProcesses()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("orca-slicer"); }
        catch { yield break; }
        foreach (var p in procs)
        {
            using (p)
                yield return Native.GetProcessPath((uint)p.Id);
        }
    }

    private static IEnumerable<string?> FromUninstallEntries()
    {
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            var found = new List<string?>();
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("DisplayName") is not string display ||
                        !display.Contains("OrcaSlicer", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (sub.GetValue("InstallLocation") is string loc && loc.Length > 0)
                        found.Add(Path.Combine(loc.Trim('"'), OrcaWindows.ExeName));
                    if (sub.GetValue("DisplayIcon") is string icon && icon.Length > 0)
                        found.Add(icon.Split(',')[0].Trim('"'));
                    if (sub.GetValue("UninstallString") is string un && un.Length > 0)
                    {
                        var dir = Path.GetDirectoryName(un.Trim().Trim('"').Split('"')[0]);
                        if (dir != null)
                            found.Add(Path.Combine(dir, OrcaWindows.ExeName));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading {path}: {ex.Message}");
            }
            foreach (var f in found)
                if (f != null && f.EndsWith(OrcaWindows.ExeName, StringComparison.OrdinalIgnoreCase))
                    yield return f;
        }
    }

    private static string? ReadDefault(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return (key?.GetValue(null) as string)?.Trim('"');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ask once with a file dialog; the answer is saved in settings.</summary>
    public static string? FindOrAsk(Settings settings)
    {
        var found = Find(settings);
        if (found != null)
            return found;

        Log.Warn("orca-slicer.exe not found automatically; asking the user.");
        using var dlg = new OpenFileDialog
        {
            Title = "Where is orca-slicer.exe?",
            Filter = "OrcaSlicer|orca-slicer.exe|Programs (*.exe)|*.exe",
            FileName = OrcaWindows.ExeName,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK)
            return null;
        settings.OrcaPath = dlg.FileName;
        settings.Save();
        return dlg.FileName;
    }

    /// <summary>
    /// Start a new OrcaSlicer with the given files/links. There is no working "--no-single-instance" flag
    /// (docs/orca-ipc.md §5), so this opens a new window only while Orca's
    /// "Allow only one OrcaSlicer instance" preference is off.
    /// </summary>
    public static Process Launch(string exe, IReadOnlyList<string> forwards)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        foreach (var f in forwards)
            psi.ArgumentList.Add(f);
        Log.Info($"Launching {exe} {string.Join(" ", forwards.Select(f => "\"" + f + "\""))}");
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
    }

    /// <summary>
    /// Orca's "Allow only one OrcaSlicer instance" preference (app.single_instance in
    /// %APPDATA%\OrcaSlicer\OrcaSlicer.conf). Returns null if it can't be read. Only that one value is read.
    /// </summary>
    public static bool? ReadSingleInstancePreference()
    {
        try
        {
            string conf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrcaSlicer", "OrcaSlicer.conf");
            if (!File.Exists(conf))
                return null;
            string text;
            using (var fs = new FileStream(conf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                text = reader.ReadToEnd();
            // The JSON is followed by "# MD5 checksum ..." — cut after the last closing brace.
            int end = text.LastIndexOf('}');
            if (end < 0)
                return null;
            using var doc = JsonDocument.Parse(text.AsMemory(0, end + 1), new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("app", out var app) && app.TryGetProperty("single_instance", out var v))
            {
                return v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => v.GetString() is "true" or "1",
                    _ => null,
                };
            }
            return false; // Orca's default (AppConfig.cpp:193)
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read Orca's single_instance preference: {ex.Message}");
            return null;
        }
    }
}
