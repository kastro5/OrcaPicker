using OrcaPicker;

namespace OrcaPicker.Tests;

/// <summary>
/// Round-trip harness: OrcaPicker's escaping → the C# port of Orca's unescape_strings_cstyle
/// → exactly the original arguments, including Orca skipping argv[0].
/// </summary>
public class EscapeTests
{
    public static TheoryData<string[]> Samples => new()
    {
        new[] { @"C:\Program Files\OrcaSlicer\orca-slicer.exe", @"C:\Users\me\My Models\part.stl" },
        new[] { "orca-slicer.exe", @"C:\a\b\c.3mf", @"D:\x y\z.step", @"\\server\share\dir\m.obj" },
        new[] { "exe", "path with \"quotes\".stl" },
        new[] { "exe", @"trailing\backslash\" },
        new[] { "exe", @"C:\" },
        new[] { "exe", "semi;colon;name.stl" },
        new[] { "exe", "tab\there", "new\nline", "cr\rreturn" },
        new[] { "exe", @"C:\Users\Jürgen\Модели\部品 ① 🚀.3mf" },
        new[] { "exe", "  leading and trailing spaces  " },
        new[] { "exe", "" },
        new[] { "exe", "", "" },
        new[] { "", "a" },
        new[] { "exe", "orcaslicer://open?file=https%3A%2F%2Fexample.com%2Fa%20b.stl&x=1" },
        new[] { "exe", "bambustudio://open?file=https%3A%2F%2Fmakerworld.bblmw.com%2Fm.3mf%3Fat%3D1%26key%3Dk&name=My Benchy.3mf" },
        new[] { "exe", "simple", "also_simple.stl" },
        new[] { "exe", "\\\"\\\\\"" },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void RoundTrips(string[] args)
    {
        string escaped = CStyleEscape.EscapeStrings(args);
        Assert.True(CStyleEscape.UnescapeStrings(escaped, out var back), escaped);
        Assert.Equal(args, back);
    }

    [Fact]
    public void SingleEmptyString_IsQuoted()
    {
        Assert.Equal("\"\"", CStyleEscape.EscapeStrings([""]));
    }

    [Fact]
    public void MatchesOrcaFormat_Exactly()
    {
        // What Orca's own second instance sends for: orca-slicer.exe "C:\My Models\part.stl" plain.stl
        string msg = OrcaWindows.BuildMessage(@"C:\Program Files\OrcaSlicer\orca-slicer.exe",
            [@"C:\My Models\part.stl", "plain.stl"]);
        Assert.Equal("\"C:\\\\Program Files\\\\OrcaSlicer\\\\orca-slicer.exe\";\"C:\\\\My Models\\\\part.stl\";plain.stl", msg);
    }

    [Fact]
    public void OrcaSkipsFirstArgument()
    {
        string msg = OrcaWindows.BuildMessage("orca-slicer.exe", [@"C:\a b\c.stl"]);
        Assert.True(CStyleEscape.UnescapeStrings(msg, out var parts));
        // handle_message: "Skip the first argument, it is the path to the slicer executable."
        Assert.Equal([@"C:\a b\c.stl"], parts.Skip(1));
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("\"ends with backslash\\")]
    [InlineData("\"a\" junk")]
    public void Unescape_RejectsWhatOrcaRejects(string bad)
    {
        Assert.False(CStyleEscape.UnescapeStrings(bad, out _));
    }

    [Fact]
    public void Unescape_TrailingSemicolon_AddsEmptyString()
    {
        Assert.True(CStyleEscape.UnescapeStrings("a;", out var parts));
        Assert.Equal(["a", ""], parts);
    }

    [Fact]
    public void Fuzz_RoundTrips()
    {
        const string alphabet = "ab Z09 \t;\\\"\r\n:/.%&?=_-äöü中🚀";
        var rnd = new Random(12345);
        for (int n = 0; n < 5000; n++)
        {
            int count = rnd.Next(1, 5);
            var args = new string[count];
            for (int i = 0; i < count; i++)
            {
                int len = rnd.Next(0, 12);
                var chars = new System.Text.StringBuilder();
                for (int k = 0; k < len; k++)
                {
                    // Pick whole text elements so surrogate pairs (🚀) stay intact.
                    var elems = System.Globalization.StringInfo.GetTextElementEnumerator(alphabet);
                    int pick = rnd.Next(0, new System.Globalization.StringInfo(alphabet).LengthInTextElements);
                    for (int e = 0; e <= pick; e++) elems.MoveNext();
                    chars.Append(elems.GetTextElement());
                }
                args[i] = chars.ToString();
            }
            string escaped = CStyleEscape.EscapeStrings(args);
            Assert.True(CStyleEscape.UnescapeStrings(escaped, out var back), escaped);
            Assert.Equal(args, back);
        }
    }
}
