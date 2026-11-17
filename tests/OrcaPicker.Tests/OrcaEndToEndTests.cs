using System.Diagnostics;
using OrcaPicker;
using Xunit.Abstractions;

namespace OrcaPicker.Tests;

/// <summary>
/// Opt-in, real OrcaSlicer test (set ORCAPICKER_E2E=1). It starts its OWN Orca window, sends it an STL via
/// OrcaPicker's WM_COPYDATA code, checks that the window title changes, and then kills
/// only the Orca process it started. Windows you already have open are never touched.
/// </summary>
public class OrcaEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public OrcaEndToEndTests(ITestOutputHelper output) => _out = output;

    private static string Title(IntPtr hwnd) =>
        OrcaWindows.Find().FirstOrDefault(w => w.Handle == hwnd)?.Title ?? "(gone)";

    private static bool Enabled => Environment.GetEnvironmentVariable("ORCAPICKER_E2E") == "1";

    private const string CubeStl = """
        solid cube
        facet normal 0 0 -1
         outer loop
          vertex 0 0 0
          vertex 10 10 0
          vertex 10 0 0
         endloop
        endfacet
        facet normal 0 0 -1
         outer loop
          vertex 0 0 0
          vertex 0 10 0
          vertex 10 10 0
         endloop
        endfacet
        endsolid cube
        """;

    [Fact]
    public void SendsFileToASpecificOrcaWindow()
    {
        if (!Enabled)
            return; // opt-in only

        string? exe = OrcaLocator.Find(new Settings());
        Assert.NotNull(exe);

        string dir = Path.Combine(Path.GetTempPath(), "OrcaPicker e2e ü " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string stl = Path.Combine(dir, "cube; with \"spaces\" ü.stl".Replace("\"", "'"));
        File.WriteAllText(stl, CubeStl);

        var before = OrcaWindows.Find().Select(w => w.Handle).ToHashSet();
        var psi = new ProcessStartInfo(exe!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        using var orca = Process.Start(psi)!;
        try
        {
            // Wait for its main window (Instance_Hash props are set at the end of post_init).
            OrcaWindow? mine = null;
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (mine == null && DateTime.UtcNow < deadline && !orca.HasExited)
            {
                Thread.Sleep(500);
                mine = OrcaWindows.Find().FirstOrDefault(w => w.ProcessId == orca.Id && !before.Contains(w.Handle));
            }
            Assert.NotNull(mine);
            Thread.Sleep(3000); // let it settle (first-run dialogs, plater)

            string titleBefore = Title(mine!.Handle);
            var result = OrcaWindows.Send(mine!, [stl]);
            Assert.True(result.Ok, result.Message);

            // Orca's info log is usually off (log_severity_level in OrcaSlicer.conf overrides --debug),
            // so check the visible effect: loading a model into the empty project changes the window title.
            string titleAfter = titleBefore;
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (titleAfter == titleBefore && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                titleAfter = Title(mine!.Handle);
            }
            _out.WriteLine($"title before: '{titleBefore}'  after: '{titleAfter}'");
            Assert.NotEqual(titleBefore, titleAfter);
            Assert.True(OrcaWindows.Find().Any(w => w.Handle == mine!.Handle), "window disappeared");
        }
        finally
        {
            try { if (!orca.HasExited) orca.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
