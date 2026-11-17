using System.Diagnostics;

namespace OrcaPicker;

internal static class Dialogs
{
    /// <summary>Error with optional "Copy link" / "Open in browser" (for links) and any extra buttons.</summary>
    /// <returns>The text of the button that was clicked.</returns>
    public static string Error(string heading, string text, InputItem? link = null, params string[] extraButtons)
    {
        Log.Warn($"Error shown: {heading} — {text.ReplaceLineEndings(" ")}");
        var page = new TaskDialogPage
        {
            Caption = "OrcaPicker",
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Error,
            AllowCancel = true,
        };
        var extras = extraButtons.Select(b => new TaskDialogButton(b)).ToList();
        foreach (var b in extras)
            page.Buttons.Add(b);

        string? url = link?.DownloadUrl ?? (link?.Kind == InputKind.Link ? link.Raw : null);
        TaskDialogButton? copy = null, browse = null;
        if (link != null)
        {
            copy = new TaskDialogButton("Copy link") { AllowCloseDialog = false };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(url ?? link.Raw); copy.Text = "Copied"; }
                catch (Exception ex) { Log.Warn($"Clipboard: {ex.Message}"); }
            };
            page.Buttons.Add(copy);
            if (url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                browse = new TaskDialogButton("Open in browser");
                page.Buttons.Add(browse);
            }
        }
        page.Buttons.Add(TaskDialogButton.Close);

        var result = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        if (result == browse && url != null)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log.Warn($"Open in browser: {ex.Message}"); }
        }
        return result.Text ?? "";
    }

    public static void Info(string heading, string text)
    {
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = "OrcaPicker",
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
        }, TaskDialogStartupLocation.CenterScreen);
    }
}

/// <summary>
/// Non-blocking notice: a tray balloon (shown as a toast on Windows 10/11).
/// Used when something needs saying but no picker window is shown.
/// </summary>
internal static class Notifier
{
    private static NotifyIcon? _icon;
    private static DateTime _visibleUntil;

    public static bool IsShowing => _icon != null && DateTime.UtcNow < _visibleUntil;

    public static void Show(string title, string text, Action? onClick = null)
    {
        try
        {
            _icon?.Dispose();
            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = "OrcaPicker",
                Visible = true,
            };
            if (onClick != null)
                _icon.BalloonTipClicked += (_, _) => onClick();
            _icon.ShowBalloonTip(6000, title, text, ToolTipIcon.Info);
            _visibleUntil = DateTime.UtcNow.AddSeconds(6);
        }
        catch (Exception ex)
        {
            Log.Warn($"Notifier: {ex.Message}");
        }
    }

    public static void Hide()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
