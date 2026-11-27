using Chaos.Display;

namespace Chaos.Launcher;

/// <summary>An item in the resolution drop-down.</summary>
public sealed record ResolutionChoice(Resolution Resolution)
{
    public string Label =>
        Resolution.IsFullScreen ? "Full screen"
        : Resolution.IsWindow ? $"{Resolution.Width} x {Resolution.Height}"
        : "Default";

    public bool Sharp => Resolution.IsSharp;

    public override string ToString() => Label;
}
