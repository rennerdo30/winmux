namespace WinMux.Terminal;

/// <summary>A coherent copy of the app's wheel reporting and alternate-screen requests.</summary>
public readonly record struct TerminalMouseMode(
    bool Reporting, bool Sgr, bool AlternateScreen, bool AlternateScroll, bool ApplicationCursorKeys);
