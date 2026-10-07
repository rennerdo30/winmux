using Avalonia;
using WinMux.Core.Settings;

namespace WinMux.Shell.Tests;

public sealed class RenderingOptionsTests
{
    [Fact]
    public void Automatic_requests_GPU_then_software_fallback()
    {
        Assert.Equal([Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            RenderingOptions.Create(RenderingPreference.Automatic).RenderingMode);
    }

    [Fact]
    public void Software_does_not_attempt_a_GPU_backend()
    {
        Assert.Equal([Win32RenderingMode.Software],
            RenderingOptions.Create(RenderingPreference.Software).RenderingMode);
    }
}
