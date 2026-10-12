using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinMux.PaneHost.Tests;

public sealed class HostStartupTests
{
    [Fact]
    public async Task Failed_adoption_never_exposes_a_black_host_window()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "WinMux.PaneHost.exe");
        using var host = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--adopt", "1", "--strategy", "attach" },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = await host.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.StartsWith("HOST_HWND=", first);
            var hwnd = new IntPtr(long.Parse(first!["HOST_HWND=".Length..]));
            Assert.True(IsWindow(hwnd));
            Assert.False(IsWindowVisible(hwnd));

            Assert.Equal("ERROR=that window has closed", await host.StandardOutput.ReadLineAsync(timeout.Token));
            host.StandardInput.WriteLine("DETACH");
            host.StandardInput.Flush();
            Assert.Equal("DETACHED", await host.StandardOutput.ReadLineAsync(timeout.Token));
            await host.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (!host.HasExited) host.Kill(); // Only this test's process.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
