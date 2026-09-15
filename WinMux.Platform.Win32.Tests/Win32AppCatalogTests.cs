using WinMux.Platform;
using WinMux.Platform.Win32.Windows;

namespace WinMux.Platform.Win32.Tests;

/// <summary>
/// The catalogue against the real Start Menu.
///
/// Asserted as properties rather than exact contents, because what is installed differs on every
/// machine. The properties are the ones the picker depends on: everything offered can actually be
/// launched, and nothing is offered twice.
/// </summary>
public sealed class Win32AppCatalogTests
{
    private readonly Win32AppCatalog _catalog = new();

    [Fact]
    public void The_machine_has_applications()
    {
        var apps = _catalog.List(out var error);

        Assert.True(apps.Count > 0, "no Start Menu entries resolved: " + (error ?? "no error reported"));
    }

    [Fact]
    public void Every_entry_points_at_an_executable_that_exists()
    {
        // A picker offering something that cannot start is worse than a shorter list.
        var apps = _catalog.List(out _);

        Assert.All(apps, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            Assert.EndsWith(".exe", app.Program, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(app.Program), $"{app.Name} points at missing {app.Program}");
        });
    }

    [Fact]
    public void The_same_program_is_not_offered_twice()
    {
        // The machine-wide and per-user Start Menus overlap heavily.
        var apps = _catalog.List(out _);

        var keys = apps.Select(app => app.Program + "|" + app.Arguments).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Entries_are_sorted_by_name_so_the_picker_is_predictable()
    {
        var names = _catalog.List(out _).Select(app => app.Name).ToArray();

        Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
    }

    [Fact]
    public void Uninstallers_are_not_offered()
    {
        var apps = _catalog.List(out _);

        Assert.DoesNotContain(apps, app => app.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_executable_resolves_to_itself()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        var app = _catalog.Resolve(notepad, out var error);

        Assert.True(app is not null, error);
        Assert.Equal(notepad, app!.Value.Program, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_path_that_does_not_exist_is_refused_with_a_reason()
    {
        var app = _catalog.Resolve(@"C:\no\such\thing.exe", out var error);

        Assert.Null(app);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void An_empty_path_is_refused_rather_than_throwing()
    {
        Assert.Null(_catalog.Resolve("   ", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Listing_twice_gives_the_same_answer()
    {
        // The picker re-lists when reopened; a catalogue that shuffled would be maddening.
        Assert.Equal(
            _catalog.List(out _).Select(a => a.Program + "|" + a.Name),
            _catalog.List(out _).Select(a => a.Program + "|" + a.Name));
    }
}
