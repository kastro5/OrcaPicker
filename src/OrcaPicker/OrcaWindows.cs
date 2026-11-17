using System.Runtime.InteropServices;
using System.Text;

namespace OrcaPicker;

/// <summary>An OrcaSlicer main window that can receive files.</summary>
internal sealed record OrcaWindow(IntPtr Handle, uint ProcessId, string Title, bool Minimized, string? ExePath);

internal sealed class SendResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public static readonly SendResult Success = new() { Ok = true };
}

/// <summary>Finds Orca main windows and talks to them with Orca's own WM_COPYDATA hand-off (docs/orca-ipc.md).</summary>
internal static class OrcaWindows
{
    public const string ExeName = "orca-slicer.exe";

    /// <summary>
    /// All OrcaSlicer main windows, in z-order (topmost / most recently active first).
    /// A main window is a top-level "wxWindowNR" window of an orca-slicer.exe process that carries
    /// the "Instance_Hash_Minor" property (set by init_windows_properties once Orca has finished starting).
    /// </summary>
    public static List<OrcaWindow> Find()
    {
        var result = new List<OrcaWindow>();
        var exeCache = new Dictionary<uint, string?>();
        uint mySession = 0;
        Native.ProcessIdToSessionId((uint)Environment.ProcessId, out mySession);
        var className = new StringBuilder(256);

        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                className.Clear();
                if (Native.GetClassName(hwnd, className, className.Capacity) == 0 || className.ToString() != "wxWindowNR")
                    return true;
                if (Native.GetProp(hwnd, "Instance_Hash_Minor") == IntPtr.Zero && Native.GetProp(hwnd, "Instance_Hash_Major") == IntPtr.Zero)
                    return true;
                if (!Native.IsWindowVisible(hwnd))
                    return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (Native.ProcessIdToSessionId(pid, out uint session) && session != mySession)
                    return true;
                if (!exeCache.TryGetValue(pid, out var exe))
                    exeCache[pid] = exe = Native.GetProcessPath(pid);
                // If the path can't be read, still accept it: the class + property check is what Orca itself uses.
                if (exe != null && !Path.GetFileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    return true;

                result.Add(new OrcaWindow(hwnd, pid, GetTitle(hwnd), Native.IsIconic(hwnd), exe));
            }
            catch (Exception ex)
            {
                Log.Warn($"EnumWindows callback: {ex.Message}");
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = Native.GetWindowTextLength(hwnd);
        var sb = new StringBuilder(Math.Max(len + 1, 256));
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        string t = sb.ToString().Trim();
        return t.Length > 0 ? t : "OrcaSlicer";
    }

    /// <summary>The message Orca's second instance would send: escape_strings_cstyle({argv0, args...}).</summary>
    public static string BuildMessage(string argv0, IEnumerable<string> args)
    {
        var all = new List<string> { argv0 };
        all.AddRange(args);
        return CStyleEscape.EscapeStrings(all);
    }

    /// <summary>Send files/links to a running Orca window, then bring it to the front.</summary>
    public static SendResult Send(OrcaWindow target, IReadOnlyList<string> forwards)
    {
        if (!Native.IsWindow(target.Handle))
            return new SendResult { Message = "That OrcaSlicer window has closed." };

        string message = BuildMessage(target.ExePath ?? ExeName, forwards);
        Log.Info($"WM_COPYDATA -> hwnd 0x{target.Handle:X} pid {target.ProcessId}: {message}");

        // Let Orca take the foreground: its handler calls Show()/Raise().
        Native.AllowSetForegroundWindow(target.ProcessId);

        IntPtr buffer = Marshal.StringToHGlobalUni(message); // null-terminated UTF-16
        try
        {
            var cds = new Native.COPYDATASTRUCT
            {
                dwData = (IntPtr)1,
                cbData = (message.Length + 1) * sizeof(char), // includes the terminator, as Orca does
                lpData = buffer,
            };
            IntPtr ok = Native.SendMessageTimeout(target.Handle, Native.WM_COPYDATA, IntPtr.Zero, ref cds,
                Native.SMTO_ABORTIFHUNG | Native.SMTO_ERRORONEXIT, 10_000, out _);
            if (ok == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Log.Error($"SendMessageTimeout failed, Win32 error {err}");
                return new SendResult { Message = Explain(err, target) };
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        Native.ForceForeground(target.Handle);
        return SendResult.Success;
    }

    private static string Explain(int err, OrcaWindow target)
    {
        bool? orcaElevated = Native.IsProcessElevated(target.ProcessId);
        bool meElevated = Native.IsCurrentProcessElevated();
        if (err == Native.ERROR_ACCESS_DENIED || (orcaElevated == true && !meElevated))
            return "Windows blocked the message to OrcaSlicer.\n\n" +
                   "This usually means OrcaSlicer is running as administrator while OrcaPicker is not. " +
                   "Windows doesn't let normal programs send messages to elevated ones (User Interface Privilege Isolation).\n\n" +
                   "Close OrcaSlicer and start it normally (not with \"Run as administrator\"), " +
                   "and make sure its compatibility settings don't force administrator mode.";
        if (err == Native.ERROR_TIMEOUT || err == 0)
            return "OrcaSlicer didn't respond within 10 seconds. It may be busy (slicing, a dialog open) or hung.\n\n" +
                   "Finish what it's doing and try again.";
        return $"Couldn't send to OrcaSlicer (Windows error {err}).";
    }
}
