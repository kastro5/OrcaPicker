namespace OrcaPicker;

/// <summary>
/// "Where should this model go?" A list of open Orca windows (most recently active first, preselected)
/// plus "New window". Enter or double-click confirms, Esc cancels, 1-9 picks directly.
/// </summary>
internal sealed class PickerForm : FitForm
{
    private sealed record Choice(OrcaWindow? Window, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly ListBox _list;

    /// <summary>The chosen window, or null for "New window". Only meaningful when DialogResult is OK.</summary>
    public OrcaWindow? SelectedWindow { get; private set; }

    public PickerForm(IReadOnlyList<InputItem> items, IReadOnlyList<OrcaWindow> windows, IReadOnlyList<string> notices)
    {
        Text = "OrcaPicker";
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        SetContent(layout);

        layout.Controls.Add(new Label
        {
            Text = "Send to OrcaSlicer",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 2),
        });

        layout.Controls.Add(new Label
        {
            Text = DescribeItems(items),
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 2),
        });

        string source = DescribeSource(items);
        if (source.Length > 0)
            layout.Controls.Add(new Label
            {
                Text = source,
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 8),
            });

        foreach (var notice in notices)
            layout.Controls.Add(new Label
            {
                Text = "⚠ " + notice,
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                BackColor = Color.FromArgb(255, 244, 206),
                ForeColor = Color.FromArgb(90, 60, 0),
                Padding = new Padding(6, 4, 6, 4),
                Margin = new Padding(0, 0, 0, 6),
            });

        _list = new ListBox
        {
            Width = 520,
            IntegralHeight = false,
            Margin = new Padding(0, 4, 0, 10),
        };
        int n = 1;
        foreach (var w in windows)
            _list.Items.Add(new Choice(w, $"{(n <= 9 ? n++ + "   " : "    ")}{w.Title}{(w.Minimized ? "   (minimized)" : "")}"));
        _list.Items.Add(new Choice(null, "+   New window"));
        _list.SelectedIndex = 0; // most recently active window
        _list.DoubleClick += (_, _) => { if (_list.SelectedItem != null) Confirm(); };
        layout.Controls.Add(_list);

        // Bottom row: "Settings…" on the left, Open / Cancel on the right.
        var bottom = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var settings = new Button { Text = "Settings…", AutoSize = true, MinimumSize = new Size(90, 0), Anchor = AnchorStyles.Left };
        settings.Click += (_, _) => OpenSettings();
        bottom.Controls.Add(settings, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
        var ok = new Button { Text = "Open", AutoSize = true, MinimumSize = new Size(90, 0) };
        ok.Click += (_, _) => Confirm();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        bottom.Controls.Add(buttons, 1, 0);
        layout.Controls.Add(bottom);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Open OrcaPicker's settings on top of the picker; the pick continues when it closes.</summary>
    private void OpenSettings()
    {
        Log.Info("Settings opened from the picker.");
        try
        {
            using var form = new SettingsForm();
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error("Settings from picker failed", ex);
            Dialogs.Error("Couldn't open settings", ex.Message);
        }
        _list.Focus();
    }

    private void Confirm()
    {
        if (_list.SelectedItem is not Choice c)
            return;
        SelectedWindow = c.Window;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 1-9: pick a window directly. N: new window.
        int digit = e.KeyCode is >= Keys.D1 and <= Keys.D9 ? e.KeyCode - Keys.D1
                  : e.KeyCode is >= Keys.NumPad1 and <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad1 : -1;
        if (digit >= 0 && digit < _list.Items.Count - 1)
        {
            _list.SelectedIndex = digit;
            Confirm();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.N && !e.Control && !e.Alt)
        {
            _list.SelectedIndex = _list.Items.Count - 1;
            Confirm();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Make every row visible (up to 10). This is measured on the real handle, after the font and DPI apply;
    /// a height computed in the constructor came out one row short.
    /// </summary>
    private void SizeList()
    {
        int rows = Math.Min(_list.Items.Count, 10);
        int border = _list.Height - _list.ClientSize.Height;
        _list.Height = _list.ItemHeight * rows + border + 2;
    }

    protected override void OnLoad(EventArgs e)
    {
        SizeList();
        base.OnLoad(e); // FitForm sizes the window around the content
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        BeginInvoke(SizeList); // queued before FitForm's re-fit, so the window is sized around the new list
        base.OnDpiChanged(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Native.ForceForeground(Handle);
        _list.Focus();
    }

    internal static string DescribeItems(IReadOnlyList<InputItem> items)
    {
        var names = items.Select(i => i.DisplayName).ToList();
        return names.Count switch
        {
            0 => "(nothing)",
            <= 3 => string.Join(", ", names),
            _ => $"{string.Join(", ", names.Take(3))} +{names.Count - 3} more",
        };
    }

    private static string DescribeSource(IReadOnlyList<InputItem> items)
    {
        var first = items.FirstOrDefault();
        if (first == null)
            return "";
        if (first.Kind == InputKind.LocalFile)
            return Path.GetDirectoryName(first.Forward) ?? "";
        if (first.DownloadUrl != null && Uri.TryCreate(first.DownloadUrl, UriKind.Absolute, out var u))
            return $"Download from {u.Host} (OrcaSlicer downloads it)";
        return "";
    }
}

/// <summary>Small "waiting for OrcaSlicer to start" window with a way out.</summary>
internal sealed class WaitForm : FitForm
{
    public WaitForm(string message, Func<bool> done, TimeSpan timeout)
    {
        Text = "OrcaPicker";
        ControlBox = false;
        TopMost = true;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 10) });
        layout.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 420, Margin = new Padding(0, 0, 0, 10) });
        var skip = new Button { Text = "Don't wait", AutoSize = true, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(skip);
        SetContent(layout);
        CancelButton = skip;

        var started = DateTime.UtcNow;
        var timer = new System.Windows.Forms.Timer { Interval = 300 };
        timer.Tick += (_, _) =>
        {
            if (done() || DateTime.UtcNow - started > timeout)
            {
                timer.Stop();
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        Shown += (_, _) => timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }
}
