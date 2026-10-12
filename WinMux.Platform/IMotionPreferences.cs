namespace WinMux.Platform;

/// <summary>Whether short client-area transitions are allowed by the user's system setting.</summary>
public interface IMotionPreferences
{
    bool ClientAreaAnimationEnabled { get; }
}
