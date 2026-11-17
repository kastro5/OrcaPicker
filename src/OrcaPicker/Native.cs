using System.Runtime.InteropServices;
using System.Text;

namespace OrcaPicker;

internal static class Native
{
    public const int WM_COPYDATA = 0x004A;
    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint SMTO_ERRORONEXIT = 0x0020;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_TIMEOUT = 1460;
    public const uint ASFW_ANY = unchecked((uint)-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    public static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    /// <summary>Full image path of a process, or null. Works for elevated processes too.</summary>
    public static string? GetProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>True/false if the elevation of the process could be read, null otherwise.</summary>
    public static bool? IsProcessElevated(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out var token))
                return null;
            try
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool IsCurrentProcessElevated() => IsProcessElevated((uint)Environment.ProcessId) == true;

    /// <summary>
    /// Bring a window to the foreground, working around the foreground lock:
    /// restore if minimized, try SetForegroundWindow, and if Windows refuses, temporarily
    /// attach to the input of the current foreground thread and try again.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        else
            ShowWindow(hwnd, SW_SHOW);

        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
            return;

        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        bool attachedFg = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != myThread && AttachThreadInput(myThread, targetThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(myThread, targetThread, false);
            if (attachedFg) AttachThreadInput(myThread, fgThread, false);
        }
        if (GetForegroundWindow() == hwnd)
            return;

        // Last resort: Windows lets the process that generated the last input event set the foreground.
        // A synthetic Alt tap counts as such an event.
        keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
