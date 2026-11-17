namespace OrcaPicker;

/// <summary>Shown when OrcaPicker.exe runs with no arguments.</summary>
internal sealed class SettingsForm : FitForm
{
    private static readonly Dictionary<string, string> SchemeHelp = new()
    {
        ["orcaslicer"] = "orcaslicer://   Printables \"Open in OrcaSlicer\", other sites",
        ["bambustudio"] = "bambustudio://   MakerWorld \"Open in Bambu Studio\"",
        ["prusaslicer"] = "prusaslicer://   Printables \"Open in PrusaSlicer\"",
    };

    private readonly Settings _settings;
    private readonly Dictionary<string, CheckBox> _schemeBoxes = new();
    private readonly Dictionary<string, Label> _schemeStatus = new();
    private readonly CheckBox _watcher;
    private readonly TextBox _orcaPath;
    private readonly Label _orcaDetected;
    private readonly Label _status;

    public SettingsForm()
    {
        _settings = Settings.Load();

        Text = "OrcaPicker settings";
        MinimizeBox = true;

        var root = new TableLayoutPanel { ColumnCount = 1 };
        SetContent(root);

        // --- Web links ---
        var links = Group("Web links handled by OrcaPicker");
        var linksTable = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        foreach (var scheme in InputParser.RegisteredSchemes)
        {
            var cb = new CheckBox { Text = SchemeHelp[scheme], AutoSize = true, Checked = _settings.IsSchemeEnabled(scheme) };
            var st = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 5, 0, 0), MaximumSize = new Size(330, 0) };
            _schemeBoxes[scheme] = cb;
            _schemeStatus[scheme] = st;
            linksTable.Controls.Add(cb);
            linksTable.Controls.Add(st);
        }
        links.Controls.Add(linksTable);

        _watcher = new CheckBox
        {
            Text = "Keep these registrations (a watcher at logon puts them back if OrcaSlicer takes them)",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Checked = _settings.Watcher,
            Margin = new Padding(3, 8, 3, 3),
        };
        links.Controls.Add(_watcher);

        var linkButtons = Flow();
        var register = new Button { Text = "Register", AutoSize = true };
        register.Click += (_, _) => DoRegister();
        var unregister = new Button { Text = "Unregister all", AutoSize = true };
        unregister.Click += (_, _) => DoUnregister();
        linkButtons.Controls.Add(register);
        linkButtons.Controls.Add(unregister);
        links.Controls.Add(linkButtons);
        root.Controls.Add(links.Parent!);

        // --- OrcaSlicer ---
        var orca = Group("OrcaSlicer");
        var pathRow = Flow();
        pathRow.Controls.Add(new Label { Text = "orca-slicer.exe override:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        _orcaPath = new TextBox { Width = 380, Text = _settings.OrcaPath ?? "", PlaceholderText = "(auto-detect)" };
        pathRow.Controls.Add(_orcaPath);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "OrcaSlicer|orca-slicer.exe|Programs (*.exe)|*.exe", FileName = OrcaWindows.ExeName };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _orcaPath.Text = dlg.FileName;
        };
        pathRow.Controls.Add(browse);
        orca.Controls.Add(pathRow);
        _orcaDetected = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(640, 0) };
        orca.Controls.Add(_orcaDetected);
        root.Controls.Add(orca.Parent!);

        // --- Fusion 360 ---
        var fusion = Group("Fusion 360");
        fusion.Controls.Add(new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Width = 640,
            Height = 100,
            TabStop = false,
            Text =
                "1. In Fusion, right-click a body or component (or Utilities → Make → 3D Print).\r\n" +
                "2. In the 3D Print dialog, tick \"Send to 3D Print Utility\".\r\n" +
                "3. In the Print Utility list choose \"Custom\" and browse to:\r\n" +
                "      " + AppPaths.ExePath + "\r\n" +
                "4. Click OK. Fusion exports the file and OrcaPicker asks which Orca window gets it.",
        });
        root.Controls.Add(fusion.Parent!);

        // --- Bottom ---
        _status = new Label { AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(3, 8, 3, 3) };
        root.Controls.Add(_status);

        var bottom = Flow();
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => { SaveFromUi(); RefreshStatus("Saved."); };
        var logs = new Button { Text = "Open log folder", AutoSize = true };
        logs.Click += (_, _) => Registration.OpenAppFolder();
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(save);
        bottom.Controls.Add(logs);
        bottom.Controls.Add(close);
        root.Controls.Add(bottom);
        CancelButton = close;

        RefreshStatus(null);
    }

    private static FlowLayoutPanel Group(string title)
    {
        var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(720, 0), Padding = new Padding(8, 8, 8, 0), Margin = new Padding(3, 3, 3, 8) };
        var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Location = new Point(10, 24), WrapContents = false, Margin = new Padding(0) };
        box.Controls.Add(inner);
        return inner;
    }

    private static FlowLayoutPanel Flow() =>
        new() { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };

    private void SaveFromUi()
    {
        foreach (var (scheme, cb) in _schemeBoxes)
            _settings.Schemes[scheme] = cb.Checked;
        _settings.Watcher = _watcher.Checked;
        string path = _orcaPath.Text.Trim().Trim('"');
        _settings.OrcaPath = path.Length == 0 ? null : path;
        _settings.Save();
        if (_settings.Registered)
            Registration.SetWatcher(_settings.Watcher);
    }

    private void DoRegister()
    {
        try
        {
            SaveFromUi();
            var report = Registration.Register(_settings);
            RefreshStatus("Registered: " + string.Join(", ", report));
        }
        catch (Exception ex)
        {
            Log.Error("Register failed", ex);
            RefreshStatus("Register failed: " + ex.Message);
        }
    }

    private void DoUnregister()
    {
        try
        {
            SaveFromUi();
            var report = Registration.UnregisterAll(_settings);
            RefreshStatus(report.Count == 0 ? "Nothing to unregister." : "Restored: " + string.Join(", ", report));
        }
        catch (Exception ex)
        {
            Log.Error("Unregister failed", ex);
            RefreshStatus("Unregister failed: " + ex.Message);
        }
    }

    private void RefreshStatus(string? message)
    {
        foreach (var (scheme, label) in _schemeStatus)
        {
            string handler = Registration.CurrentHandler(scheme);
            label.Text = "→ " + handler;
            label.ForeColor = handler == "OrcaPicker" ? Color.DarkGreen : SystemColors.GrayText;
        }

        var exe = OrcaLocator.Find(_settings);
        bool? single = OrcaLocator.ReadSingleInstancePreference();
        _orcaDetected.Text =
            "Using: " + (exe ?? "not found — set the path above") + "\n" +
            "Orca's \"Allow only one OrcaSlicer instance\": " +
            (single switch { true => "ON — turn it off, or \"New window\" can't open a new window", false => "off (good)", _ => "unknown" }) + "\n" +
            "Watcher: " + (Watcher.IsRunning() ? "running" : "not running") +
            (Registration.IsWatcherInstalled() ? ", starts at logon" : "");

        _status.Text = message ?? (_settings.Registered ? "" : "Not registered yet. Click Register to make OrcaPicker handle these links.");
    }
}
