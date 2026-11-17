using System.Text.RegularExpressions;

namespace OrcaPicker;

internal enum InputKind { LocalFile, Link, Unknown }

/// <summary>One thing to hand to Orca: a local file or a link that Orca's own downloader understands.</summary>
/// <param name="Raw">The argument exactly as received.</param>
/// <param name="Forward">The string to pass to Orca (a full path, or a normalized link).</param>
/// <param name="DisplayName">Short name to show in the picker.</param>
/// <param name="DownloadUrl">For links: the decoded http(s) URL of the model.</param>
/// <param name="Scheme">For links: the lower-case scheme the link arrived with.</param>
internal sealed record InputItem(
    string Raw,
    InputKind Kind,
    string Forward,
    string DisplayName,
    string? DownloadUrl = null,
    string? Scheme = null);

/// <summary>
/// Forgiving parser for everything that arrives on the command line.
/// Rules (see docs/orca-ipc.md §4):
///  - an existing local path (or file:// URI) is forwarded as a full path;
///  - a link that already has Orca's exact form ("scheme://open?file=" / "open/?file=" / "bambustudioopen://"),
///    and whose value decodes once to an http(s) URL, is forwarded unchanged apart from lower-casing the prefix;
///  - anything else that contains a model URL (file=/url=/... parameter, or any embedded http(s) URL,
///    single or double encoded) is rebuilt as "&lt;scheme&gt;://open?file=&lt;encoded url&gt;".
/// </summary>
internal static class InputParser
{
    /// <summary>The schemes OrcaPicker registers for.</summary>
    public static readonly string[] RegisteredSchemes = ["orcaslicer", "bambustudio", "prusaslicer"];

    /// <summary>Schemes Orca's downloader accepts in the "scheme://open?file=" form.</summary>
    private static readonly string[] OrcaOpenSchemes = ["orcaslicer", "prusaslicer", "bambustudio", "cura"];

    // Same patterns as InstanceCheck.cpp:517-518 / Downloader.cpp.
    public static readonly Regex OrcaOpenRegex =
        new(@"^(orcaslicer|prusaslicer|cura|bambustudio)://open/?\?file=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Regex OrcaOpenRegex2 =
        new(@"^(bambustudioopen)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SchemeRegex =
        new(@"^([a-z][a-z0-9+.\-]*):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ParamRegex =
        new(@"[?&/](file|url|uri|link|model|download|path)=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EmbeddedUrlRegex =
        new(@"https?(://|%3A%2F%2F|%253A%252F%252F)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True if Orca (running or starting) would treat this string as a download link.</summary>
    public static bool OrcaAcceptsLink(string s) => OrcaOpenRegex.IsMatch(s) || OrcaOpenRegex2.IsMatch(s);

    public static InputItem Parse(string raw, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1].Trim();

        // 1. Local file (Fusion 360 export, drag and drop, file association).
        if (TryLocalFile(s, fileExists, out var localPath))
            return new InputItem(raw, InputKind.LocalFile, localPath, Path.GetFileName(localPath));

        var schemeMatch = SchemeRegex.Match(s);
        string scheme = schemeMatch.Success ? schemeMatch.Groups[1].Value.ToLowerInvariant() : "";

        // 2. Already in the exact form Orca expects: pass through untouched.
        var exact = OrcaOpenRegex.Match(s);
        if (!exact.Success)
            exact = OrcaOpenRegex2.Match(s);
        if (exact.Success)
        {
            string value = s[exact.Length..];
            string once = SafeUnescape(value);
            if (IsHttpUrl(once))
            {
                SplitBambuName(once, out var url, out var name);
                string forward = exact.Value.ToLowerInvariant() + value;
                return new InputItem(raw, InputKind.Link, forward, name ?? NameFromUrl(url), url, scheme);
            }
        }

        // 3. Forgiving: find the URL wherever it is and rebuild a canonical link.
        if (TryExtractUrl(s, out var downloadUrl, out var displayName))
        {
            string outScheme = Array.IndexOf(OrcaOpenSchemes, scheme) >= 0 ? scheme : "orcaslicer";
            string forward = $"{outScheme}://open?file={Uri.EscapeDataString(downloadUrl)}";
            if (outScheme == "bambustudio" && displayName != null)
                forward += "&name=" + Uri.EscapeDataString(displayName);
            return new InputItem(raw, InputKind.Link, forward, displayName ?? NameFromUrl(downloadUrl), downloadUrl,
                scheme.Length > 0 ? scheme : null);
        }

        return new InputItem(raw, InputKind.Unknown, raw, raw);
    }

    private static bool TryLocalFile(string s, Func<string, bool> fileExists, out string path)
    {
        path = "";
        if (s.Length < 3)
            return false;
        try
        {
            if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.IsFile)
                s = uri.LocalPath;
            // Only things that look like Windows paths (C:\..., \\server\...), not "scheme://...".
            bool looksLikePath = (s.Length > 2 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
                                 || s.StartsWith(@"\\", StringComparison.Ordinal)
                                 || !s.Contains(':');
            if (!looksLikePath || !fileExists(s))
                return false;
            path = Path.GetFullPath(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractUrl(string s, out string url, out string? name)
    {
        url = "";
        name = null;
        int start = -1;

        var p = ParamRegex.Match(s);
        if (p.Success)
        {
            int valueStart = p.Index + p.Length;
            // The value must contain a URL somewhere near its start (allow for encoding).
            var inValue = EmbeddedUrlRegex.Match(s, valueStart);
            if (inValue.Success && inValue.Index == valueStart)
                start = valueStart;
        }
        if (start < 0)
        {
            var e = EmbeddedUrlRegex.Match(s);
            if (e.Success)
                start = e.Index;
        }
        if (start < 0)
            return false;

        string value = s[start..];
        int ws = value.IndexOfAny([' ', '\t', '\r', '\n', '"']);
        if (ws >= 0)
            value = value[..ws];

        // If the value is encoded, a raw '&' ends it (an encoded URL has its own '&' as %26).
        // Bambu-style links may carry "&name=<file name>" after it.
        if (!IsHttpUrl(value))
        {
            int amp = value.IndexOf('&');
            if (amp >= 0)
            {
                string rest = value[amp..];
                value = value[..amp];
                var nm = Regex.Match(rest, @"[&]name=([^&]*)", RegexOptions.IgnoreCase);
                if (nm.Success)
                    name = NullIfEmpty(DecodeFully(nm.Groups[1].Value));
            }
        }

        string decoded = DecodeFully(value);
        if (!IsHttpUrl(decoded))
            return false;
        SplitBambuName(decoded, out url, out var innerName);
        name ??= innerName;
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    /// <summary>Percent-decode until the value starts with http(s)://, at most twice.</summary>
    internal static string DecodeFully(string value)
    {
        string v = value;
        for (int i = 0; i < 2 && !IsHttpUrl(v); i++)
        {
            string next = SafeUnescape(v);
            if (next == v)
                break;
            v = next;
        }
        // Still something like "name%20with%20spaces" after reaching the URL? Leave it: it is part of the URL.
        return v;
    }

    /// <summary>MakerWorld's "url&amp;name=file.3mf" convention (Plater::import_model_id).</summary>
    private static void SplitBambuName(string decoded, out string url, out string? name)
    {
        int i = decoded.IndexOf("&name=", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            url = decoded;
            name = null;
            return;
        }
        url = decoded[..i];
        name = NullIfEmpty(SafeUnescape(decoded[(i + 6)..]));
    }

    /// <summary>File name for display: last path segment of the URL, or the host.</summary>
    internal static string NameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        string path = uri.AbsolutePath.TrimEnd('/');
        string last = SafeUnescape(path[(path.LastIndexOf('/') + 1)..]);
        // Some sites put the name in the query: ...?filename=part.stl
        if (!last.Contains('.'))
        {
            var q = Regex.Match(uri.Query, @"[?&](file)?name=([^&]+)", RegexOptions.IgnoreCase);
            if (q.Success)
                return SafeUnescape(q.Groups[2].Value);
        }
        return last.Length > 0 ? last : uri.Host;
    }

    private static bool IsHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
