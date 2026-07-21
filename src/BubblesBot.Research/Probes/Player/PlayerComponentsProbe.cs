using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Player;

/// <summary>
/// The player's component map resolves the components the bot depends on. Anchors the whole
/// entity -> details -> component-lookup chain; a failure here means component resolution broke.
/// </summary>
public sealed class PlayerComponentsProbe : IProbe
{
    private static readonly (string Comp, string OracleKey)[] Wanted =
    {
        ("Life", "player.life"),
        ("Render", "player.render"),
        ("Positioned", "player.positioned"),
        ("Actor", "player.actor"),
        ("Player", "player.playercomp"),
    };

    public string Name => "player.components";
    public string Group => "player";
    public string Description => "Player component map resolves Life/Render/Positioned/Actor/Player.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
        => ProbeResult.Combine(Wanted.Select(w =>
        {
            var addr = ctx.Chain.PlayerComponent(w.Comp);
            return addr == 0
                ? ProbeResult.Fail($"component '{w.Comp}' missing from map")
                : Check.Address(ctx, w.OracleKey, addr, $"player.{w.Comp}", requireNonNull: true, a => Reads.Readable(ctx.Reader, a));
        }).ToArray());

    public ProbeResult Discover(ProbeContext ctx)
        // Component instances aren't at a fixed offset (resolved via the name->index map); the
        // discovery story here is the map walk itself, which Validate already exercises.
        => ProbeResult.Found("player.components", []);
}
