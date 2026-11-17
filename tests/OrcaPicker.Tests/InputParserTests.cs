using OrcaPicker;

namespace OrcaPicker.Tests;

public class InputParserTests
{
    private const string PrintablesUrl =
        "https://files.printables.com/media/prints/3161/stls/37440_d1a8f7f2-1b5e-4bfa-9a8a/3DBenchy.stl";
    private static readonly string Enc1 = Uri.EscapeDataString(PrintablesUrl);
    private static readonly string Enc2 = Uri.EscapeDataString(Enc1);

    // MakerWorld-style: signed URL with its own query string, plus "&name=" (Plater::import_model_id).
    private const string MakerWorldUrl =
        "https://makerworld.bblmw.com/makerworld/model/US1a2b3c/design/2024-05-01_abc.3mf?at=1715000000&exp=1715003600&key=k1&uid=42";

    private static InputItem Parse(string raw) => InputParser.Parse(raw, _ => false);

    /// <summary>What Orca's Downloader would end up downloading (Downloader.cpp: substr after match, unescape once).</summary>
    private static string OrcaWouldDownload(string forward)
    {
        var m = InputParser.OrcaOpenRegex.Match(forward);
        if (!m.Success)
            m = InputParser.OrcaOpenRegex2.Match(forward);
        Assert.True(m.Success, $"Orca would reject: {forward}");
        string decoded = Uri.UnescapeDataString(forward[m.Length..]);
        int name = decoded.IndexOf("&name=", StringComparison.Ordinal);
        return name >= 0 && forward.StartsWith("bambustudio", StringComparison.Ordinal) ? decoded[..name] : decoded;
    }

    /// <summary>Orca's startup check is case-sensitive (Utils.hpp is_supported_open_protocol).</summary>
    private static void AssertOrcaStartupAccepts(string forward) =>
        Assert.True(
            forward.StartsWith("orcaslicer://open", StringComparison.Ordinal) ||
            forward.StartsWith("prusaslicer://open", StringComparison.Ordinal) ||
            forward.StartsWith("bambustudio://open", StringComparison.Ordinal) ||
            forward.StartsWith("bambustudioopen://", StringComparison.Ordinal) ||
            forward.StartsWith("cura://open", StringComparison.Ordinal),
            forward);

    [Theory]
    [InlineData("orcaslicer")]
    [InlineData("prusaslicer")]
    [InlineData("bambustudio")]
    public void SingleEncoded_IsForwardedUnchanged(string scheme)
    {
        foreach (var sep in new[] { "open?file=", "open/?file=" })
        {
            string raw = $"{scheme}://{sep}{Enc1}";
            var item = Parse(raw);
            Assert.Equal(InputKind.Link, item.Kind);
            Assert.Equal(raw, item.Forward);
            Assert.Equal(PrintablesUrl, item.DownloadUrl);
            Assert.Equal("3DBenchy.stl", item.DisplayName);
            Assert.Equal(scheme, item.Scheme);
            Assert.Equal(PrintablesUrl, OrcaWouldDownload(item.Forward));
            AssertOrcaStartupAccepts(item.Forward);
        }
    }

    [Theory]
    [InlineData("orcaslicer")]
    [InlineData("prusaslicer")]
    [InlineData("bambustudio")]
    public void DoubleEncoded_IsRebuiltSingleEncoded(string scheme)
    {
        foreach (var sep in new[] { "open?file=", "open/?file=" })
        {
            var item = Parse($"{scheme}://{sep}{Enc2}");
            Assert.Equal(InputKind.Link, item.Kind);
            Assert.Equal($"{scheme}://open?file={Enc1}", item.Forward);
            Assert.Equal(PrintablesUrl, item.DownloadUrl);
            Assert.Equal("3DBenchy.stl", item.DisplayName);
            Assert.Equal(PrintablesUrl, OrcaWouldDownload(item.Forward));
        }
    }

    [Fact]
    public void Unencoded_Url_IsForwardedUnchanged()
    {
        string raw = "orcaslicer://open?file=https://example.com/models/my%20part.stl";
        var item = Parse(raw);
        Assert.Equal(raw, item.Forward);
        Assert.Equal("my part.stl", item.DisplayName);
    }

    [Fact]
    public void MixedCaseScheme_IsLowerCased_ForOrcasCaseSensitiveStartupCheck()
    {
        var item = Parse($"OrcaSlicer://Open?file={Enc1}");
        Assert.Equal($"orcaslicer://open?file={Enc1}", item.Forward);
        AssertOrcaStartupAccepts(item.Forward);
    }

    [Fact]
    public void MakerWorld_WithRawName_KeepsNameAndForwardsUnchanged()
    {
        string raw = $"bambustudio://open?file={Uri.EscapeDataString(MakerWorldUrl)}&name=Benchy%20Remix.3mf";
        var item = Parse(raw);
        Assert.Equal(InputKind.Link, item.Kind);
        Assert.Equal(raw, item.Forward);
        Assert.Equal(MakerWorldUrl, item.DownloadUrl);
        Assert.Equal("Benchy Remix.3mf", item.DisplayName);
        Assert.Equal(MakerWorldUrl, OrcaWouldDownload(item.Forward));
    }

    [Fact]
    public void MakerWorld_DoubleEncoded_WithName_IsRebuiltWithName()
    {
        string raw = $"bambustudio://open/?file={Uri.EscapeDataString(Uri.EscapeDataString(MakerWorldUrl))}&name=Benchy.3mf";
        var item = Parse(raw);
        Assert.Equal(MakerWorldUrl, item.DownloadUrl);
        Assert.Equal("Benchy.3mf", item.DisplayName);
        Assert.Equal($"bambustudio://open?file={Uri.EscapeDataString(MakerWorldUrl)}&name=Benchy.3mf", item.Forward);
        Assert.Equal(MakerWorldUrl, OrcaWouldDownload(item.Forward));
    }

    [Fact]
    public void BambuStudioOpen_IsForwardedUnchanged()
    {
        string raw = "bambustudioopen://" + Enc1;
        var item = Parse(raw);
        Assert.Equal(InputKind.Link, item.Kind);
        Assert.Equal(raw, item.Forward);
        Assert.Equal(PrintablesUrl, OrcaWouldDownload(item.Forward));
    }

    [Theory]
    [InlineData("orcaslicer://open?url={0}", "orcaslicer")]
    [InlineData("orcaslicer://import?file={0}", "orcaslicer")]
    [InlineData("prusaslicer://download/{0}", "prusaslicer")]
    [InlineData("prusaslicer://open?foo=1&file={0}", "prusaslicer")]
    [InlineData("someslicer://whatever/{0}", "orcaslicer")]
    [InlineData("{1}", "orcaslicer")]
    public void OddFormats_FindTheEmbeddedUrl(string template, string expectedScheme)
    {
        foreach (var enc in new[] { Enc1, Enc2 })
        {
            string raw = string.Format(template, enc, PrintablesUrl);
            var item = Parse(raw);
            Assert.Equal(InputKind.Link, item.Kind);
            Assert.Equal(PrintablesUrl, item.DownloadUrl);
            Assert.StartsWith($"{expectedScheme}://open?file=", item.Forward);
            Assert.Equal(PrintablesUrl, OrcaWouldDownload(item.Forward));
            AssertOrcaStartupAccepts(item.Forward);
        }
    }

    [Fact]
    public void EncodedValue_FollowedByOtherRawParams_StopsAtAmpersand()
    {
        var item = Parse($"orcaslicer://open?url={Enc1}&source=printables");
        Assert.Equal(PrintablesUrl, item.DownloadUrl);
    }

    [Fact]
    public void WindowsAppendedTrailingSlash_IsHandled()
    {
        // Windows sometimes appends "/" to protocol URLs (see Downloader.cpp comment on the "mysterious slash").
        var item = Parse($"orcaslicer://open/?file={Enc1}");
        Assert.Equal(PrintablesUrl, item.DownloadUrl);
    }

    [Theory]
    [InlineData("orcaslicer://open")]
    [InlineData("orcaslicer://open/?file=")]
    [InlineData("orcaslicer://open?file=not-a-url")]
    [InlineData("hello world")]
    [InlineData("")]
    public void Garbage_IsUnknown(string raw)
    {
        Assert.Equal(InputKind.Unknown, Parse(raw).Kind);
    }

    [Fact]
    public void LocalFile_IsForwardedAsFullPath()
    {
        string path = @"C:\Users\me\AppData\Local\Temp\Fusion 360\My Part (v2).stl";
        var item = InputParser.Parse(path, p => p == path);
        Assert.Equal(InputKind.LocalFile, item.Kind);
        Assert.Equal(path, item.Forward);
        Assert.Equal("My Part (v2).stl", item.DisplayName);
    }

    [Fact]
    public void LocalFile_InQuotes_AndFileUri()
    {
        string path = @"C:\models\ä ö\part.3mf";
        Assert.Equal(InputKind.LocalFile, InputParser.Parse($"\"{path}\"", p => p == path).Kind);
        var fromUri = InputParser.Parse(new Uri(path).AbsoluteUri, p => p == path);
        Assert.Equal(InputKind.LocalFile, fromUri.Kind);
        Assert.Equal(path, fromUri.Forward);
    }

    [Fact]
    public void LocalFile_RealFileOnDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OrcaPicker tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "ünïcode part.stl");
            File.WriteAllText(file, "solid x\nendsolid x\n");
            var item = InputParser.Parse(file);
            Assert.Equal(InputKind.LocalFile, item.Kind);
            Assert.Equal(file, item.Forward);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UnquotedPathSplitIntoSeveralArgs_IsJoined()
    {
        string path = @"C:\Users\me\Fusion Exports\my part.stl";
        var items = PickerApp.ParseArgs(path.Split(' '), p => p == path);
        var item = Assert.Single(items);
        Assert.Equal(InputKind.LocalFile, item.Kind);
        Assert.Equal(path, item.Forward);
    }

    [Theory]
    [InlineData("https://files.printables.com/media/prints/1/stls/2/Big%20Benchy.3mf", "Big Benchy.3mf")]
    [InlineData("https://example.com/dl?id=5&filename=part%201.stl", "part 1.stl")]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://example.com/models/ünï.obj?sig=abc", "ünï.obj")]
    public void DisplayName_FromUrl(string url, string expected)
    {
        Assert.Equal(expected, InputParser.NameFromUrl(url));
    }

    [Fact]
    public void Registration_ExeFromCommand()
    {
        Assert.Equal(@"C:\Program Files\OrcaSlicer\orca-slicer.exe",
            Registration.ExeFromCommand("\"C:\\Program Files\\OrcaSlicer\\orca-slicer.exe\" \"%1\""));
        Assert.Equal(@"C:\Program Files\Bambu Studio\bambu-studio.exe",
            Registration.ExeFromCommand("\"C:\\Program Files\\Bambu Studio\\bambu-studio.exe\"  \"%1\""));
        Assert.Equal(@"C:\Tools\x.exe", Registration.ExeFromCommand(@"C:\Tools\x.exe %1"));
    }
}
