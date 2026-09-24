using WinMux.Core.Update;

namespace WinMux.Shell.Update;

/// <summary>
/// Whether to put an available update in front of the user, now.
///
/// <para>
/// Separate from the window because it is a rule rather than a dialog, and the rule is the part
/// that can be wrong. The check runs on a timer and on demand, so without one the same offer would
/// arrive over and over until it was accepted — and an application that asks again every few
/// minutes is one people learn to dismiss without reading, which is the same as never asking.
/// </para>
/// </summary>
internal sealed class UpdateOffer
{
    private ReleaseVersion? _offered;

    /// <summary>
    /// True the first time a version is seen, false every time after. A <em>newer</em> version is
    /// offered again: declining 0.7.7 is not a decision about 0.8.0.
    /// </summary>
    public bool ShouldOffer(ReleaseVersion version)
    {
        if (_offered == version) return false;
        _offered = version;
        return true;
    }

    /// <summary>
    /// Forget what has been offered, for when the user asks to check by name. Asking for a check
    /// and getting silence because of a decision made an hour ago is a check that did nothing.
    /// </summary>
    public void Reset() => _offered = null;
}
