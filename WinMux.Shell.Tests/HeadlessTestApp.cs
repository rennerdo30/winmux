using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace WinMux.Shell.Tests;

/// <summary>
/// The Avalonia application these tests run inside.
///
/// Deliberately not <c>WinMux.Shell.App</c>. That one reads the user's settings and profiles off
/// disk during <c>Initialize</c>, asks the platform for its colour scheme and installs a theme from
/// the result — reasonable for the product, wrong for a test, which would then depend on whatever
/// happens to be in the current user's profile directory. This installs the Fluent theme and stops,
/// which is the only part the controls under test need in order to have templates at all.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Runs a test body on Avalonia's UI thread, in a headless platform.
///
/// <para>
/// <c>Avalonia.Headless.XUnit</c> supplies an <c>[AvaloniaFact]</c> that does this, and depends on
/// xunit v3 while this project is on v2. Rather than migrate the whole suite for one attribute, the
/// session underneath it is used directly — it is public API and it is what the attribute calls.
/// </para>
///
/// <para>
/// The session is shared for the whole assembly. Starting one per test would be correct and slow:
/// each start builds an application, and these tests only construct controls rather than mutating
/// global state that would leak between them.
/// </para>
/// </summary>
public static class Headless
{
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Headless).Assembly);

    /// <summary>
    /// Run <paramref name="body"/> on the UI thread and surface whatever it throws.
    /// </summary>
    /// <remarks>
    /// There is deliberately **no** <c>RunAsync(Action)</c> overload beside this one, and the
    /// synchronous entry point is called something else. With both present, an
    /// <c>async () =&gt; { … }</c> lambda binds to the <c>Action</c> overload and becomes
    /// <c>async void</c>: the body runs, every exception inside it is lost, and the test passes no
    /// matter what it asserts. That is not hypothetical — this helper shipped that way for about ten
    /// minutes, and a deliberate <c>Assert.Fail</c> in the first test went green. A test harness
    /// that cannot fail is worse than no harness, because it is counted.
    /// </remarks>
    public static Task RunAsync(Func<Task> body) => Session.Dispatch(
        async () =>
        {
            await body();

            // A value, so this binds to the Task-returning overload of Dispatch. The void one does
            // not await what the body returns: a throw *after* an await is dropped, and so is a
            // failed assertion, which makes every async test in the file pass unconditionally.
            // Measured, not assumed — DispatchProbeTests pins both halves.
            return true;
        },
        CancellationToken.None);

    /// <summary>Run a synchronous body on the UI thread. Named apart from <see cref="RunAsync"/>
    /// so that an async lambda can never bind to it by accident.</summary>
    public static Task RunSync(Action body) => Session.Dispatch(
        () =>
        {
            body();
            return Task.CompletedTask;
        },
        CancellationToken.None);
}
