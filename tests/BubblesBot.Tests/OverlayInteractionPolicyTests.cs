using BubblesBot.Bot.Modes;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class OverlayInteractionPolicyTests
{
    [Theory]
    [InlineData(MechanicKind.Shrine, MechanicStatus.Available, true)]
    [InlineData(MechanicKind.Shrine, MechanicStatus.Completed, false)]
    [InlineData(MechanicKind.RitualRune, MechanicStatus.Available, false)]
    [InlineData(MechanicKind.EldritchAltar, MechanicStatus.Available, false)]
    public void InteractKeyAllowsOnlySafeImmediateMechanics(
        MechanicKind kind, MechanicStatus status, bool expected)
    {
        var mechanic = new MechanicEntry(kind, new EntityCache.Entry(), status);
        Assert.Equal(expected, OverlayMode.IsSafeManualInteraction(mechanic));
    }
}
