using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class MechanicsViewTests
{
    [Fact]
    public void ConsumedShrineUsesDedicatedAvailabilityNotTargetability()
    {
        var shrine = Entry(1, "Metadata/Shrines/Shrine", EntityListReader.EntityKind.Shrine);
        shrine.IsTargetable = true; // live PoE keeps this true after taking a shrine
        shrine.Targetability = BooleanObservation.Known(
            true, "Targetable.IsTargetable", 1, ObservationConfidence.Experimental);
        shrine.ShrineAvailable = BooleanObservation.Known(
            false, "Shrine.IsAvailable", 1, ObservationConfidence.Validated);

        var mechanic = View(shrine).Entries.Single();

        Assert.Equal(MechanicStatus.Completed, mechanic.Status);
        Assert.True(mechanic.IsActivated);
        Assert.Empty(View(shrine).Available(MechanicKind.Shrine));
    }

    [Fact]
    public void FreshRitualRequiresInteractionEnabled()
    {
        var ritual = Ritual(currentState: 1, interactionEnabled: 0);
        Assert.Equal(MechanicStatus.Unknown, View(ritual).Entries.Single().Status);

        ritual.RitualInteractionEnabled = LongObservation.Known(
            1, "Ritual.interaction_enabled", 2, ObservationConfidence.Validated);

        Assert.Equal(MechanicStatus.Available, View(ritual).Entries.Single().Status);
    }

    [Theory]
    [InlineData(2, MechanicStatus.Active)]
    [InlineData(3, MechanicStatus.Completed)]
    public void RitualCurrentStateDrivesLifecycle(long raw, MechanicStatus expected)
    {
        var ritual = Ritual(raw, interactionEnabled: 0);
        Assert.Equal(expected, View(ritual).Entries.Single().Status);
    }

    [Theory]
    [InlineData(0, MechanicStatus.Available)]
    [InlineData(1, MechanicStatus.Completed)]
    public void DeliriumMirrorInteractedDrivesLifecycle(long raw, MechanicStatus expected)
    {
        var mirror = Entry(3,
            "Metadata/MiscellaneousObjects/Affliction/AfflictionInitiator",
            EntityListReader.EntityKind.Unknown);
        mirror.DeliriumMirrorInteracted = LongObservation.Known(
            raw, "Delirium.interacted", 1, ObservationConfidence.Validated);

        var mechanic = View(mirror).Entries.Single();

        Assert.Equal(MechanicKind.DeliriumMirror, mechanic.Kind);
        Assert.Equal(expected, mechanic.Status);
    }

    [Fact]
    public void DeliriumMirrorFailsClosedWhenStateIsUnknown()
    {
        var mirror = Entry(4,
            "Metadata/MiscellaneousObjects/Affliction/AfflictionInitiator",
            EntityListReader.EntityKind.Unknown);
        Assert.Equal(MechanicStatus.Unknown, View(mirror).Entries.Single().Status);
    }

    private static EntityCache.Entry Ritual(long currentState, long interactionEnabled)
    {
        var entry = Entry(2, "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable",
            EntityListReader.EntityKind.Unknown);
        entry.RitualCurrentState = LongObservation.Known(
            currentState, "Ritual.current_state", 1, ObservationConfidence.Validated);
        entry.RitualInteractionEnabled = LongObservation.Known(
            interactionEnabled, "Ritual.interaction_enabled", 1, ObservationConfidence.Validated);
        return entry;
    }

    private static EntityCache.Entry Entry(uint id, string path, EntityListReader.EntityKind kind) => new()
    {
        Id = id,
        Path = path,
        Metadata = path,
        Kind = kind,
        HpCurrent = 1,
        HpMax = 1,
    };

    private static MechanicsView View(params EntityCache.Entry[] entries)
        => new(entries);
}
