using OrcaPicker;

namespace OrcaPicker.Tests;

/// <summary>
/// Opt-in UI smoke test (set ORCAPICKER_UI=1). It shows a picker in-process (no single-instance lock, nothing is
/// sent anywhere), clicks "Settings…", checks that the settings window opens over it, then closes both.
/// Set ORCAPICKER_UI_SHOT=&lt;folder&gt; to save screenshots of both windows.
/// </summary>
public class PickerUiTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ORCAPICKER_UI") == "1";

    [Fact]
    public void SettingsButton_OpensSettingsOverThePicker()
    {
        if (!Enabled)
            return;

        string? shotDir = Environment.GetEnvironmentVariable("ORCAPICKER_UI_SHOT");
        Exception? error = null;
        bool settingsOpened = false, settingsOwnedByPicker = false, pickerStillOpenAfter = false;

        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                var item = InputParser.Parse(@"C:\Models\Test Part.stl", _ => true);
                using var picker = new PickerForm([item], [], []);
                var timer = new System.Windows.Forms.Timer { Interval = 700 };
                int step = 0;
                timer.Tick += (_, _) =>
                {
                    switch (++step)
                    {
                        case 1:
                            Save(picker, shotDir, "ui-picker.png");
                            // PerformClick runs ShowDialog; this timer keeps ticking inside its modal loop.
                            Find<Button>(picker, "Settings…").PerformClick();
                            pickerStillOpenAfter = picker.Visible && !picker.IsDisposed;
                            break;
                        case 2:
                            var s = Application.OpenForms.OfType<SettingsForm>().FirstOrDefault();
                            settingsOpened = s is { Visible: true };
                            settingsOwnedByPicker = s?.Owner == picker;
                            if (s != null)
                            {
                                Save(s, shotDir, "ui-settings-from-picker.png");
                                s.Close();
                            }
                            break;
                        default:
                            timer.Stop();
                            picker.Close();
                            break;
                    }
                };
                picker.Shown += (_, _) => timer.Start();
                Application.Run(picker);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI test timed out");
        Assert.Null(error);
        Assert.True(settingsOpened, "Settings window did not open");
        Assert.True(settingsOwnedByPicker, "Settings window should be owned by the picker (so it stays above it)");
        Assert.True(pickerStillOpenAfter, "Picker should still be open after closing settings");
    }

    private static T Find<T>(Control root, string text) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t && c.Text == text)
                return t;
            var inner = FindOrNull<T>(c, text);
            if (inner != null)
                return inner;
        }
        throw new InvalidOperationException($"No {typeof(T).Name} '{text}'");
    }

    private static T? FindOrNull<T>(Control root, string text) where T : Control
    {
        try { return Find<T>(root, text); }
        catch (InvalidOperationException) { return null; }
    }

    private static void Save(Form form, string? dir, string name)
    {
        if (string.IsNullOrEmpty(dir))
            return;
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(Path.Combine(dir, name));
    }
}
