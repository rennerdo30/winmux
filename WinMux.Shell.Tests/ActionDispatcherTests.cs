using WinMux.Shell.Actions;

namespace WinMux.Shell.Tests;

public class ActionDispatcherTests
{
    [Fact]
    public void Registered_action_is_dispatched_by_its_stable_name()
    {
        var calls = 0;
        var dispatcher = new ActionDispatcher();
        dispatcher.Register(ShellActionNames.SplitColumns, () => calls++);

        var result = dispatcher.Dispatch("SPLIT-COLUMNS");

        Assert.Equal(ActionDispatchStatus.Executed, result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Missing_action_returns_a_surfaceable_failure()
    {
        var result = new ActionDispatcher().Dispatch(ShellActionNames.SaveSession);

        Assert.Equal(ActionDispatchStatus.NotRegistered, result.Status);
        Assert.Contains(ShellActionNames.SaveSession, result.Error);
    }

    [Fact]
    public void Handler_exception_is_returned_instead_of_being_silently_lost()
    {
        var dispatcher = new ActionDispatcher();
        dispatcher.Register(ShellActionNames.ClosePane, () => throw new InvalidOperationException("busy"));

        var result = dispatcher.Dispatch(ShellActionNames.ClosePane);

        Assert.Equal(ActionDispatchStatus.Failed, result.Status);
        Assert.Contains("busy", result.Error);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    public void Every_registered_action_has_a_unique_name()
    {
        Assert.Equal(ShellActionNames.All.Count, ShellActionNames.All.Distinct().Count());
        Assert.Contains(ShellActionNames.ToggleForeignHostStrategy, ShellActionNames.All);
    }
}
