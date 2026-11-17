namespace OrcaPicker;

/// <summary>
/// A dialog that sizes itself to one content panel. Form.AutoSize doesn't follow per-monitor DPI changes
/// (e.g. opening on a 150% monitor next to a 100% one), so the size is recomputed on load, on show,
/// and on every DPI change.
/// </summary>
internal class FitForm : Form
{
    private const int Margin96 = 14;
    private Control? _content;

    protected FitForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(AppPaths.ExePath); } catch { }
    }

    /// <summary>Set the single auto-sizing panel that holds everything.</summary>
    protected void SetContent(Control content)
    {
        _content = content;
        content.AutoSize = true;
        Controls.Add(content);
    }

    private void Fit()
    {
        if (_content == null)
            return;
        int m = LogicalToDeviceUnits(Margin96);
        _content.PerformLayout();
        var pref = _content.GetPreferredSize(Size.Empty);
        _content.Location = new Point(m, m);
        _content.Size = pref;
        ClientSize = new Size(pref.Width + 2 * m, pref.Height + 2 * m);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Fit();
        if (StartPosition == FormStartPosition.CenterScreen)
            CenterToScreen();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Fit();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(Fit); // after WinForms has rescaled the controls
    }
}
