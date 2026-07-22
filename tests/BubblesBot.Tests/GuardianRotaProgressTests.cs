using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class GuardianRotaProgressTests
{
    [Fact]
    public void PartialWitnessStateSelectsFirstMissingGuardianInRotationOrder()
    {
        var progress = new GuardianRotaProgress(3);
        var objective = progress.Decide(States(
            GuardianWitnessStatus.Witnessed,
            GuardianWitnessStatus.NeedsWitness,
            GuardianWitnessStatus.NeedsWitness,
            GuardianWitnessStatus.Witnessed));

        Assert.Equal(GuardianRotaObjectiveKind.GuardianMap, objective.Kind);
        Assert.Equal("Maze of the Minotaur", objective.Name);
        Assert.Equal(1, objective.RotationNumber);
    }

    [Fact]
    public void AllWitnessedSelectsInvitationAndThreeInvitationsFinishCampaign()
    {
        var progress = new GuardianRotaProgress(3);
        var witnessed = States(
            GuardianWitnessStatus.Witnessed,
            GuardianWitnessStatus.Witnessed,
            GuardianWitnessStatus.Witnessed,
            GuardianWitnessStatus.Witnessed);

        for (var rotation = 1; rotation <= 3; rotation++)
        {
            var objective = progress.Decide(witnessed);
            Assert.Equal(GuardianRotaObjectiveKind.FormedInvitation, objective.Kind);
            Assert.Equal(rotation, objective.RotationNumber);
            progress.RecordInvitationClear();
        }

        Assert.Equal(GuardianRotaObjectiveKind.Finished, progress.Decide(witnessed).Kind);
        Assert.Equal(3, progress.RotationsCompleted);
    }

    [Fact]
    public void PortalBudgetAllowsSixEntriesAndOnlyFiveRecoverableDeaths()
    {
        var progress = new GuardianRotaProgress(1);
        Assert.True(progress.RecordPortalEntry());
        for (var death = 1; death <= 5; death++)
        {
            Assert.True(progress.RecordDeath());
            Assert.True(progress.RecordPortalEntry());
        }

        Assert.False(progress.RecordDeath());
        Assert.Equal(6, progress.PortalEntriesThisEncounter);
    }

    [Fact]
    public void UnknownWitnessStateFailsClosed()
    {
        var progress = new GuardianRotaProgress(1);
        Assert.Throws<InvalidOperationException>(() => progress.Decide(States(
            GuardianWitnessStatus.Witnessed,
            GuardianWitnessStatus.Unknown,
            GuardianWitnessStatus.Witnessed,
            GuardianWitnessStatus.Witnessed)));
    }

    [Theory]
    [InlineData(1, 1, 7, 1, 1, 7)]
    [InlineData(0, 2, 8, 2, 2, 8)]
    [InlineData(99, 1, -4, 3, 3, 0)]
    [InlineData(-2, -1, 4, 0, 0, 4)]
    public void RestoreTotalsNormalizesAndBoundsDurableProgress(
        int rotations, int invitations, int maps,
        int expectedRotations, int expectedInvitations, int expectedMaps)
    {
        var progress = new GuardianRotaProgress(3);

        progress.RestoreTotals(rotations, invitations, maps);

        Assert.Equal(expectedRotations, progress.RotationsCompleted);
        Assert.Equal(expectedInvitations, progress.InvitationsCompleted);
        Assert.Equal(expectedMaps, progress.GuardianMapsCompleted);
    }

    private static IReadOnlyDictionary<string, GuardianWitnessStatus> States(
        params GuardianWitnessStatus[] values)
        => GuardianRotationPolicy.Maps.Select((map, index) => (Map: map, Status: values[index]))
            .ToDictionary(x => x.Map, x => x.Status, StringComparer.OrdinalIgnoreCase);
}
