using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class GuardianRotationPolicyTests
{
    [Fact]
    public void LivePhoenixTooltipIsClassifiedAsWitnessed()
    {
        var state = GuardianRotationPolicy.ClassifyTooltip(
        [
            "Tier: 16",
            "Forge of the Phoenix",
            "The Maven currently holds a re-creation of this map's Boss.",
            "Left-click to open this Atlas location",
        ]);

        Assert.Equal("Forge of the Phoenix", state.MapName);
        Assert.Equal(GuardianWitnessStatus.Witnessed, state.WitnessStatus);
    }

    [Fact]
    public void KnownGuardianWithoutMarkerNeedsWitness()
    {
        var state = GuardianRotationPolicy.ClassifyTooltip(
            ["Tier: 16", "Lair of the Hydra", "Left-click to open this Atlas location"]);

        Assert.Equal("Lair of the Hydra", state.MapName);
        Assert.Equal(GuardianWitnessStatus.NeedsWitness, state.WitnessStatus);
    }

    [Fact]
    public void UnrelatedOrIncompleteTooltipFailsClosed()
    {
        var state = GuardianRotationPolicy.ClassifyTooltip(
            [GuardianRotationPolicy.WitnessedTooltipMarker]);

        Assert.Empty(state.MapName);
        Assert.Equal(GuardianWitnessStatus.Unknown, state.WitnessStatus);
    }

    [Fact]
    public void RotationSelectsFirstUnwitnessedMapInDeclaredOrder()
    {
        var states = GuardianRotationPolicy.Maps.ToDictionary(
            x => x, _ => GuardianWitnessStatus.Witnessed, StringComparer.OrdinalIgnoreCase);
        states["Maze of the Minotaur"] = GuardianWitnessStatus.NeedsWitness;
        states["Lair of the Hydra"] = GuardianWitnessStatus.NeedsWitness;

        Assert.True(GuardianRotationPolicy.TrySelectNext(states, out var next, out var ready));
        Assert.Equal("Maze of the Minotaur", next);
        Assert.False(ready);
    }

    [Fact]
    public void FourPositiveWitnessesMakeInvitationReady()
    {
        var states = GuardianRotationPolicy.Maps.ToDictionary(
            x => x, _ => GuardianWitnessStatus.Witnessed, StringComparer.OrdinalIgnoreCase);

        Assert.True(GuardianRotationPolicy.TrySelectNext(states, out var next, out var ready));
        Assert.Null(next);
        Assert.True(ready);
    }

    [Fact]
    public void MissingMapStateDoesNotGrantProgress()
    {
        var states = new Dictionary<string, GuardianWitnessStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["Forge of the Phoenix"] = GuardianWitnessStatus.Witnessed,
        };

        Assert.False(GuardianRotationPolicy.TrySelectNext(states, out var next, out var ready));
        Assert.Null(next);
        Assert.False(ready);
    }
}
