using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Mechanics;

/// <summary>
/// Captures every loaded area-transition entity, regardless of distance. Useful for
/// distinguishing optional side-area doors from map progression and boss-arena doors.
/// </summary>
public sealed class AreaTransitionCaptureProbe : IProbe
{
    public string Name => "capture.area-transitions";
    public string Group => "capture";
    public string Description => "Dump every loaded area-transition entity (paths, components, states, and coordinates).";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx) => MechanicStateCapture.Capture(
        ctx,
        "area transitions",
        snapshot => snapshot.Kind == EntityListReader.EntityKind.AreaTransition
                 || snapshot.Path.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase)
                 || snapshot.Metadata.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase));

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);
}
