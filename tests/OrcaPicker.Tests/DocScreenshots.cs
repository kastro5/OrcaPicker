using System.Runtime.InteropServices;
using OrcaPicker;

namespace OrcaPicker.Tests;

/// <summary>
/// Regenerates the README screenshots in docs/images (set ORCAPICKER_SHOTS=&lt;folder&gt;).
/// The real forms are shown with sample data. Nothing is sent and no settings are changed. Paths under your
/// user profile are replaced with generic ones, so the images contain nothing personal.
/// </summary>
public class DocScreenshots
{
    private static string? OutDir => Environment.GetEnvironmentVariable("ORCAPICKER_SHOTS");

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private static readonly InputItem PrintablesLink = InputParser.Parse(
        "orcaslicer://open/?file=https%3A%2F%2Ffiles.printables.com%2Fmedia%2Fprints%2F3161%2Fstls%2F37440%2F3DBenchy.stl",
        _ => false);

    private static readonly InputItem MakerWorldLink = InputParser.Parse(
        "bambustudio://open/?file=https%3A%2F%2Fmakerworld.bblmw.com%2Fmakerworld%2Fmodel%2FUS123%2Fdesign%2Ftray.3mf" +
        "%3Fat%3D1%26exp%3D2%26name%3DKnitted_Texture_Tray.3mf", _ => false);

    private static readonly InputItem FusionExport = InputParser.Parse(
        @"C:\Temp\Fusion 360\3D Print\Enclosure lid v4.3mf", _ => true);

    private static readonly OrcaWindow[] Windows =
    [
        new(IntPtr.Zero, 1, "*Gridfinity baseplate 5x4 - OrcaSlicer", false, null),
        new(IntPtr.Zero, 2, "Enclosure - OrcaSlicer", false, null),
        new(IntPtr.Zero, 3, "*Untitled - OrcaSlicer", true, null),
    ];

    [Fact]
    public void Generate()
    {
        if (string.IsNullOrEmpty(OutDir))
            return;
        Directory.CreateDirectory(OutDir);

        RunUi(() =>
        {
            Capture(new PickerForm([PrintablesLink], Windows, []), "picker.png");
            Capture(new PickerForm([MakerWorldLink], Windows.Take(1).ToArray(), []), "picker-makerworld.png");
            Capture(new PickerForm([FusionExport], Windows.Take(2).ToArray(),
                ["orcaslicer:// had been taken by orca-slicer.exe — OrcaPicker took it back."]), "picker-notice.png");
            Capture(new SettingsForm(), "settings.png", Anonymize);
        });

        foreach (var name in new[] { "picker.png", "picker-makerworld.png", "picker-notice.png", "settings.png" })
            Assert.True(File.Exists(Path.Combine(OutDir!, name)), name);
    }

    /// <summary>Settings shows real paths; make them generic and show the registered state.</summary>
    private static void Anonymize(Control c)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string generic = @"C:\Users\you\AppData\Local\Programs\OrcaPicker\OrcaPicker.exe";
        if (c is Label && c.Text.StartsWith("→ ") &&
            (c.Text.Contains("OrcaPicker", StringComparison.OrdinalIgnoreCase) || c.Text.Contains(profile, StringComparison.OrdinalIgnoreCase)))
        {
            c.Text = "→ OrcaPicker";
            c.ForeColor = Color.DarkGreen;
        }
        else if (c.Text.Contains(AppPaths.ExePath, StringComparison.OrdinalIgnoreCase))
            c.Text = c.Text.Replace(AppPaths.ExePath, generic, StringComparison.OrdinalIgnoreCase);
        else if (c.Text.Contains(local, StringComparison.OrdinalIgnoreCase))
            c.Text = c.Text.Replace(local, @"C:\Users\you\AppData\Local", StringComparison.OrdinalIgnoreCase);
        else if (c.Text.Contains(profile, StringComparison.OrdinalIgnoreCase))
            c.Text = c.Text.Replace(profile, @"C:\Users\you", StringComparison.OrdinalIgnoreCase);

        if (c is Label l && l.Text.StartsWith("Not registered yet"))
            l.Text = "";
        if (c is Label w && w.Text.Contains("Watcher: not running"))
            w.Text = w.Text.Replace("Watcher: not running", "Watcher: running, starts at logon");
        foreach (Control child in c.Controls)
            Anonymize(child);
    }

    private static void Capture(Form form, string file, Action<Control>? prepare = null)
    {
        using (form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(200, 200);
            form.TopMost = true;
            form.Shown += (_, _) =>
            {
                prepare?.Invoke(form);
                var t = new System.Windows.Forms.Timer { Interval = 600 };
                t.Tick += (_, _) =>
                {
                    t.Dispose();
                    Save(form, Path.Combine(OutDir!, file));
                    form.Close();
                };
                t.Start();
            };
            form.ShowDialog();
        }
    }

    private static void Save(Form form, string path)
    {
        // Full window including the DWM frame, then crop away the invisible resize borders.
        GetWindowRect(form.Handle, out var win);
        DwmGetWindowAttribute(form.Handle, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var vis, Marshal.SizeOf<RECT>());
        using var full = new Bitmap(win.Right - win.Left, win.Bottom - win.Top);
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(form.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */);
            g.ReleaseHdc(hdc);
        }
        var crop = new Rectangle(vis.Left - win.Left, vis.Top - win.Top, vis.Right - vis.Left, vis.Bottom - vis.Top);
        if (crop.Width <= 0 || crop.Height <= 0)
            crop = new Rectangle(Point.Empty, full.Size);
        using var cropped = full.Clone(crop, full.PixelFormat);
        cropped.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RunUi(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "timed out");
        Assert.Null(error);
    }
}
