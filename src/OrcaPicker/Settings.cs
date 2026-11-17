using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrcaPicker;

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\OrcaPicker\settings.json.</summary>
internal sealed class Settings
{
    /// <summary>Path override for orca-slicer.exe (also where the "ask once" answer is saved).</summary>
    public string? OrcaPath { get; set; }

    /// <summary>Which schemes OrcaPicker should own. Only ticked schemes are registered and guarded.</summary>
    public Dictionary<string, bool> Schemes { get; set; } = new()
    {
        ["orcaslicer"] = true,
        ["bambustudio"] = true,
        ["prusaslicer"] = true,
    };

    /// <summary>Set by --register, cleared by --unregister. Auto re-registration only happens while this is true.</summary>
    public bool Registered { get; set; }

    /// <summary>Run "OrcaPicker --watch" at logon to restore registrations that other programs take back.</summary>
    public bool Watcher { get; set; } = true;

    public bool IsSchemeEnabled(string scheme) => Schemes.TryGetValue(scheme, out var on) && on;

    public IEnumerable<string> EnabledSchemes => InputParser.RegisteredSchemes.Where(IsSchemeEnabled);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                if (s != null)
                {
                    foreach (var scheme in InputParser.RegisteredSchemes)
                        s.Schemes.TryAdd(scheme, true);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read settings, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        AppPaths.EnsureDir();
        string tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}
