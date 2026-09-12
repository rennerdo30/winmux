using WinMux.Shell.Actions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMux.Shell.Keymap;

/// <summary>
/// User-configurable keymap data. Set <see cref="Prefix"/> to null and use direct bindings for a
/// completely prefix-free map, or mix direct and prefixed bindings in one table.
/// </summary>
public sealed record KeymapConfiguration
{
    public string? Prefix { get; init; } = "Ctrl+B";
    public IReadOnlyList<KeyBindingConfiguration> Bindings { get; init; } = [];

    public static KeymapConfiguration TmuxDefaults() => new()
    {
        Prefix = "Ctrl+B",
        Bindings = DefaultBindings(KeyBindingScope.Prefixed),
    };

    public static KeymapConfiguration NoPrefixDefaults() => new()
    {
        Prefix = null,
        Bindings = DefaultBindings(KeyBindingScope.Direct),
    };

    public static KeymapConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = File.ReadAllText(path);
        var configuration = JsonSerializer.Deserialize<KeymapConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        });
        return configuration ?? throw new FormatException($"Keymap '{path}' is empty.");
    }

    private static IReadOnlyList<KeyBindingConfiguration> DefaultBindings(KeyBindingScope scope) =>
    [
        new("%", ShellActionNames.SplitColumns, scope),
        new("\"", ShellActionNames.SplitRows, scope),
        new("Left", ShellActionNames.FocusLeft, scope),
        new("Right", ShellActionNames.FocusRight, scope),
        new("Up", ShellActionNames.FocusUp, scope),
        new("Down", ShellActionNames.FocusDown, scope),
        new("X", ShellActionNames.ClosePane, scope),
        new("C", ShellActionNames.NewTab, scope),
        new("N", ShellActionNames.NextTab, scope),
        new("P", ShellActionNames.PreviousTab, scope),
        new("W", ShellActionNames.SaveSession, scope),
        new("Shift+Left", ShellActionNames.ResizeLeft, scope),
        new("Shift+Right", ShellActionNames.ResizeRight, scope),
        new("Shift+Up", ShellActionNames.ResizeUp, scope),
        new("Shift+Down", ShellActionNames.ResizeDown, scope),
        new(":", ShellActionNames.ShowPalette, scope),
        new("B", ShellActionNames.SendPrefix, scope),
        new("1", ShellActionNames.NewTerminalCmd, scope),
        new("2", ShellActionNames.NewTerminalWindowsPowerShell, scope),
        new("3", ShellActionNames.NewTerminalPowerShell, scope),
        new("4", ShellActionNames.NewTerminalWsl, scope),
        new("I", ShellActionNames.ConfigureCwdReporting, scope),
        new("A", ShellActionNames.ToggleForeignHostStrategy, scope),
    ];
}
