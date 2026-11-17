using System.Text;

namespace OrcaPicker;

/// <summary>Per-user locations. Everything OrcaPicker writes lives under %LOCALAPPDATA%\OrcaPicker.</summary>
internal static class AppPaths
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrcaPicker");

    public static string LogFile => Path.Combine(Dir, "log.txt");
    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string BackupFile => Path.Combine(Dir, "backup.json");

    /// <summary>Full path of the running OrcaPicker.exe.</summary>
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OrcaPicker.exe");

    public static void EnsureDir() => Directory.CreateDirectory(Dir);
}

/// <summary>Tiny append-only log. Never throws.</summary>
internal static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureDir();
                var file = AppPaths.LogFile;
                // Keep the log small: when it grows past 2 MB, keep one old copy.
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(file, file + ".old", overwrite: true);

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId,6}] {level} {message}{Environment.NewLine}";
                // FileShare.ReadWrite: picker, second instances and the watcher may write at the same time.
                using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
