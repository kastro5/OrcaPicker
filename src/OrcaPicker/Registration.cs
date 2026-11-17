using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace OrcaPicker;

/// <summary>
/// Registers the URL schemes under HKCU\Software\Classes and restores what was there before.
/// Before the first write to a scheme, its whole HKCU key is saved to %LOCALAPPDATA%\OrcaPicker\backup.json.
/// </summary>
internal static class Registration
{
    private const string ClassesPath = @"Software\Classes";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OrcaPicker Watcher";

    public static string OurCommand => $"\"{AppPaths.ExePath}\" \"%1\"";

    // ---------- status ----------

    /// <summary>The HKCU command for a scheme, or null.</summary>
    public static string? ReadHkcuCommand(string scheme)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{scheme}\shell\open\command");
        return key?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static bool IsOurs(string scheme) =>
        string.Equals(ReadHkcuCommand(scheme)?.Trim(), OurCommand, StringComparison.OrdinalIgnoreCase);

    /// <summary>The program a scheme effectively opens (HKCR = HKCU merged over HKLM), for display.</summary>
    public static string CurrentHandler(string scheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
            if (key?.GetValue(null) is not string cmd || cmd.Trim().Length == 0)
                return "(not registered)";
            string exe = ExeFromCommand(cmd);
            if (exe.Equals(AppPaths.ExePath, StringComparison.OrdinalIgnoreCase))
                return "OrcaPicker";
            return exe;
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    internal static string ExeFromCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 0 ? cmd[1..end] : cmd.Trim('"');
        }
        int exeEnd = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd > 0 ? cmd[..(exeEnd + 4)] : cmd.Split(' ')[0];
    }

    // ---------- register / unregister ----------

    /// <summary>Register every enabled scheme (and release disabled ones we own).</summary>
    public static List<string> Register(Settings settings)
    {
        var report = new List<string>();
        var backup = LoadBackup();
        foreach (var scheme in InputParser.RegisteredSchemes)
        {
            if (settings.IsSchemeEnabled(scheme))
            {
                WriteScheme(scheme, backup);
                report.Add($"{scheme}:// → OrcaPicker");
            }
            else if (IsOurs(scheme) || backup.ContainsKey(scheme))
            {
                RestoreScheme(scheme, backup);
                report.Add($"{scheme}:// → restored ({CurrentHandler(scheme)})");
            }
        }
        SaveBackup(backup);
        settings.Registered = true;
        settings.Save();
        SetWatcher(settings.Watcher);
        return report;
    }

    /// <summary>Restore all backed-up schemes, remove our keys, remove the watcher.</summary>
    public static List<string> UnregisterAll(Settings settings)
    {
        settings.Registered = false;
        settings.Save();
        SetWatcher(false); // stop it first, so it doesn't fight the restore

        var report = new List<string>();
        var backup = LoadBackup();
        foreach (var scheme in InputParser.RegisteredSchemes)
        {
            if (!IsOurs(scheme) && !backup.ContainsKey(scheme))
                continue;
            RestoreScheme(scheme, backup);
            report.Add($"{scheme}:// → {CurrentHandler(scheme)}");
        }
        SaveBackup(backup);
        return report;
    }

    /// <summary>
    /// The quick check run on every launch (and by the watcher): if a scheme we own was taken back by another
    /// program (OrcaSlicer re-registers orcaslicer:// at every start), put OrcaPicker back.
    /// Returns a line per repaired scheme, naming who had taken it.
    /// </summary>
    public static List<string> CheckAndRepair(Settings settings)
    {
        var repaired = new List<string>();
        if (!settings.Registered)
            return repaired;
        Dictionary<string, BackupEntry>? backup = null;
        foreach (var scheme in settings.EnabledSchemes)
        {
            if (IsOurs(scheme))
                continue;
            string previous = ExeFromCommand(ReadHkcuCommand(scheme) ?? "(nothing)");
            backup ??= LoadBackup();
            WriteScheme(scheme, backup);
            repaired.Add($"{scheme}:// had been taken by {Path.GetFileName(previous)}");
            Log.Warn($"Registration for {scheme}:// pointed to '{previous}'; restored OrcaPicker.");
        }
        if (backup != null)
            SaveBackup(backup);
        return repaired;
    }

    private static void WriteScheme(string scheme, Dictionary<string, BackupEntry> backup)
    {
        if (!backup.ContainsKey(scheme))
        {
            // First time: remember what was there (or that nothing was), and persist that before writing.
            backup[scheme] = Snapshot(scheme);
            SaveBackup(backup);
            Log.Info($"Backed up HKCU\\{ClassesPath}\\{scheme} (existed: {backup[scheme].Existed})");
        }
        if (IsOurs(scheme))
            return;

        using var root = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{scheme}", writable: true);
        root.SetValue(null, $"URL:{scheme} (OrcaPicker)");
        root.SetValue("URL Protocol", "");
        // Replace the whole shell tree so no other default verb wins.
        root.DeleteSubKeyTree("shell", throwOnMissingSubKey: false);
        using (var icon = root.CreateSubKey("DefaultIcon", writable: true))
            icon.SetValue(null, $"\"{AppPaths.ExePath}\",0");
        using (var cmd = root.CreateSubKey(@"shell\open\command", writable: true))
            cmd.SetValue(null, OurCommand);
        Log.Info($"Registered {scheme}:// → {OurCommand}");
    }

    private static void RestoreScheme(string scheme, Dictionary<string, BackupEntry> backup)
    {
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true)!;
        if (backup.TryGetValue(scheme, out var entry))
        {
            classes.DeleteSubKeyTree(scheme, throwOnMissingSubKey: false);
            if (entry.Existed && entry.Key != null)
            {
                using var key = classes.CreateSubKey(scheme, writable: true);
                RestoreKey(key, entry.Key);
            }
            backup.Remove(scheme);
            Log.Info($"Restored HKCU\\{ClassesPath}\\{scheme} from backup (existed: {entry.Existed})");
        }
        else if (IsOurs(scheme))
        {
            classes.DeleteSubKeyTree(scheme, throwOnMissingSubKey: false);
            Log.Info($"Removed HKCU\\{ClassesPath}\\{scheme} (no backup)");
        }
    }

    // ---------- watcher at logon ----------

    public static bool IsWatcherInstalled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunPath);
        return run?.GetValue(RunValue) is string;
    }

    public static void SetWatcher(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
        if (on)
        {
            run.SetValue(RunValue, $"\"{AppPaths.ExePath}\" --watch");
            Watcher.StartIfNotRunning();
        }
        else
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
            Watcher.Wake(); // it re-reads settings and exits
        }
    }

    // ---------- backup file ----------

    internal sealed class BackupEntry
    {
        public bool Existed { get; set; }
        public KeySnap? Key { get; set; }
    }

    internal sealed class KeySnap
    {
        public Dictionary<string, ValueSnap> Values { get; set; } = new();
        public Dictionary<string, KeySnap> SubKeys { get; set; } = new();
    }

    internal sealed class ValueSnap
    {
        public RegistryValueKind Kind { get; set; }
        public string? Text { get; set; }
        public string[]? Multi { get; set; }
        public long? Number { get; set; }
        public string? Base64 { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static Dictionary<string, BackupEntry> LoadBackup()
    {
        try
        {
            if (File.Exists(AppPaths.BackupFile))
                return JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(File.ReadAllText(AppPaths.BackupFile), Json)
                       ?? new();
        }
        catch (Exception ex)
        {
            // Don't silently lose the user's original registrations: keep the unreadable file aside.
            Log.Error("backup.json is unreadable; keeping a copy as backup.json.bad", ex);
            try { File.Copy(AppPaths.BackupFile, AppPaths.BackupFile + ".bad", overwrite: true); } catch { }
        }
        return new();
    }

    private static void SaveBackup(Dictionary<string, BackupEntry> backup)
    {
        AppPaths.EnsureDir();
        string tmp = AppPaths.BackupFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(backup, Json));
        File.Move(tmp, AppPaths.BackupFile, overwrite: true);
    }

    private static BackupEntry Snapshot(string scheme)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{scheme}");
        return key == null ? new BackupEntry { Existed = false } : new BackupEntry { Existed = true, Key = SnapKey(key) };
    }

    private static KeySnap SnapKey(RegistryKey key)
    {
        var snap = new KeySnap();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            object? v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var vs = new ValueSnap { Kind = kind };
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString: vs.Text = v as string; break;
                case RegistryValueKind.MultiString: vs.Multi = v as string[]; break;
                case RegistryValueKind.DWord: vs.Number = Convert.ToInt64((int)v!); break;
                case RegistryValueKind.QWord: vs.Number = (long)v!; break;
                default: vs.Base64 = v is byte[] b ? Convert.ToBase64String(b) : null; break;
            }
            snap.Values[name] = vs; // "" = default value
        }
        foreach (var subName in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(subName);
            if (sub != null)
                snap.SubKeys[subName] = SnapKey(sub);
        }
        return snap;
    }

    private static void RestoreKey(RegistryKey key, KeySnap snap)
    {
        foreach (var (name, v) in snap.Values)
        {
            string? n = name.Length == 0 ? null : name;
            switch (v.Kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString: key.SetValue(n, v.Text ?? "", v.Kind); break;
                case RegistryValueKind.MultiString: key.SetValue(n, v.Multi ?? [], v.Kind); break;
                case RegistryValueKind.DWord: key.SetValue(n, unchecked((int)(v.Number ?? 0)), v.Kind); break;
                case RegistryValueKind.QWord: key.SetValue(n, v.Number ?? 0, v.Kind); break;
                case RegistryValueKind.None:
                case RegistryValueKind.Unknown:
                    key.SetValue(n, v.Base64 == null ? [] : Convert.FromBase64String(v.Base64), RegistryValueKind.Binary); break;
                default: key.SetValue(n, v.Base64 == null ? [] : Convert.FromBase64String(v.Base64), v.Kind); break;
            }
        }
        foreach (var (subName, subSnap) in snap.SubKeys)
        {
            using var sub = key.CreateSubKey(subName, writable: true);
            RestoreKey(sub, subSnap);
        }
    }

    /// <summary>Open the folder with log.txt / settings.json / backup.json.</summary>
    public static void OpenAppFolder()
    {
        AppPaths.EnsureDir();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Dir}\"") { UseShellExecute = false });
    }
}
