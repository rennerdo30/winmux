namespace WinMux.Connections;

/// <summary>
/// A setting a node either states or leaves to whatever is above it.
///
/// <para>
/// The distinction is the whole point of a connection tree, and it is not the same as "empty".
/// A host that states an empty username is saying *log in with no name*; a host that states nothing
/// is saying *whatever Production says*. Collapsing the two is how a folder stops being useful:
/// every child ends up carrying a copy of its parent's settings, and changing the folder changes
/// nothing.
/// </para>
/// </summary>
/// <typeparam name="T">The setting's type.</typeparam>
public readonly record struct Inherited<T>
{
    private readonly T? _value;

    private Inherited(T value)
    {
        _value = value;
        IsStated = true;
    }

    /// <summary>Whether this node states the setting itself.</summary>
    public bool IsStated { get; }

    /// <summary>Take the setting from further up. The default.</summary>
    public static Inherited<T> Inherit => default;

    /// <summary>State the setting here.</summary>
    public static Inherited<T> Of(T value) => new(value);

    /// <summary>The stated value, or <paramref name="fallback"/> when this node states nothing.</summary>
    public T? Or(T? fallback) => IsStated ? _value : fallback;

    /// <summary>The stated value, when there is one.</summary>
    public bool TryGet(out T value)
    {
        value = _value!;
        return IsStated;
    }

    public override string ToString() => IsStated ? $"{_value}" : "(inherited)";

    public static implicit operator Inherited<T>(T value) => Of(value);
}
