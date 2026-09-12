using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

public sealed class OscWorkingDirectoryTests
{
    [Fact]
    public void Osc99ReportsWindowsPathWithSpacesAndNonAscii()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);

        engine.Write(Encoding.UTF8.GetBytes("\e]9;9;C:\\Work folder\\日本語\\\a"));

        Assert.Equal(["C:\\Work folder\\日本語\\"], reports);
    }

    [Fact]
    public void Osc99AcceptsStTerminatorFragmentedAtEveryByte()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);
        var sequence = Encoding.UTF8.GetBytes("\e]9;9;D:\\fragmented path\e\\");

        foreach (var value in sequence)
        {
            engine.Write([value]);
        }

        Assert.Equal(["D:\\fragmented path"], reports);
    }

    [Theory]
    [InlineData("file:///C:/Users/Renn%C3%A9/Project%20One", "C:/Users/Renné/Project One")]
    [InlineData("file://localhost/C:/Users/Test", "C:/Users/Test")]
    [InlineData("file://C:/Users/Test", "C:/Users/Test")]
    [InlineData("file://wsl-host/home/renne/Project%20%E6%97%A5%E6%9C%AC%E8%AA%9E", "/home/renne/Project 日本語")]
    [InlineData("file://wsl-host/tmp/space and 日本語", "/tmp/space and 日本語")]
    public void Osc7DecodesFileUrlAndPreservesPathKind(string url, string expected)
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);

        engine.Write(Encoding.UTF8.GetBytes($"\e]7;{url}\a"));

        Assert.Equal([expected], reports);
    }

    [Fact]
    public void BelAndBothFormsOfStringTerminatorAreAccepted()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);

        engine.Write("\e]9;9;C:\\bel\a"u8);
        engine.Write("\e]9;9;C:\\seven-bit-st\e\\"u8);
        engine.Write([0x1b, (byte)']', (byte)'9', (byte)';', (byte)'9', (byte)';',
            (byte)'/', (byte)'c', (byte)'1', 0x9c]);

        Assert.Equal(["C:\\bel", "C:\\seven-bit-st", "/c1"], reports);
    }

    [Fact]
    public void MalformedAndIncompleteSequencesDoNotReportAndParserRecovers()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);

        engine.Write("\e]7;file://host/path/%ZZ\a"u8);
        engine.Write("\e]7;file://host/path/%C3%28\a"u8);
        engine.Write([0x1b, (byte)']', (byte)'7', (byte)';', (byte)'f', (byte)'i', (byte)'l',
            (byte)'e', (byte)':', (byte)'/', (byte)'/', (byte)'h', (byte)'/', 0xc3, 0x28, 0x07]);
        engine.Write("\e]7;file://host"u8);
        engine.Write("\e]9;9;relative\\path\a"u8);

        Assert.Empty(reports);

        engine.Write("\a\e]9;9;C:\\recovered\a"u8);

        Assert.Equal(["C:\\recovered"], reports);
    }

    [Fact]
    public void OversizedSequenceIsDiscardedWithoutPoisoningLaterReports()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);
        var oversized = $"\e]9;9;C:\\{new string('x', 20_000)}\e\\";

        engine.Write(Encoding.ASCII.GetBytes(oversized));
        engine.Write("\e]9;9;C:\\after-overflow\a"u8);

        Assert.Equal(["C:\\after-overflow"], reports);
    }

    [Fact]
    public void OrdinaryOscSequencesAreNotWorkingDirectories()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);

        engine.Write("\e]0;window title\a"u8);
        engine.Write("\e]8;;https://example.com\aL\e]8;;\a"u8);
        engine.Write("\e]9;8;C:\\not-cwd\a"u8);
        engine.Write("\e]7;https://example.com/not-a-file\a"u8);
        engine.Write("\e]7;file://host/path/%\a"u8);

        Assert.Empty(reports);
        Assert.Equal("window title", engine.Title);
    }

    [Fact]
    public void EmptyAndControlBearingPathsAreIgnored()
    {
        var engine = new TerminalEmulationEngine(40, 4);
        var reports = CaptureReports(engine);

        engine.Write("\e]9;9;\a"u8);
        engine.Write("\e]9;9;C:\\line\nfeed\a"u8);
        engine.Write("\e]7;file://host/%00bad\a"u8);

        Assert.Empty(reports);
    }

    private static List<string> CaptureReports(ITerminalEngine engine)
    {
        var reports = new List<string>();
        engine.WorkingDirectoryChanged += reports.Add;
        return reports;
    }
}
