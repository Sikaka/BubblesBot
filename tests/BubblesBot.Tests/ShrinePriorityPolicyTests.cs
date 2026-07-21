using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class ShrinePriorityPolicyTests
{
    [Fact]
    public void Nearby_shrine_may_preempt_combat_when_it_covers_the_engaged_pack()
        => Assert.True(ShrinePriorityPolicy.ShouldPreempt(
            exclusiveMechanicOwnsControl: false,
            hasEngagedTarget: true,
            shrineDistanceToPlayer: 55f,
            shrineDistanceToTarget: 20f));

    [Fact]
    public void Streamed_distant_shrine_is_not_globally_urgent()
        => Assert.False(ShrinePriorityPolicy.ShouldPreempt(
            exclusiveMechanicOwnsControl: false,
            hasEngagedTarget: true,
            shrineDistanceToPlayer: 230f,
            shrineDistanceToTarget: 15f));

    [Fact]
    public void Shrine_unrelated_to_current_engagement_does_not_preempt()
        => Assert.False(ShrinePriorityPolicy.ShouldPreempt(
            exclusiveMechanicOwnsControl: false,
            hasEngagedTarget: true,
            shrineDistanceToPlayer: 40f,
            shrineDistanceToTarget: 90f));

    [Fact]
    public void Available_shrine_without_an_active_engagement_uses_normal_sweep()
        => Assert.False(ShrinePriorityPolicy.ShouldPreempt(
            exclusiveMechanicOwnsControl: false,
            hasEngagedTarget: false,
            shrineDistanceToPlayer: 10f,
            shrineDistanceToTarget: 10f));

    [Fact]
    public void Exclusive_mechanic_vetoes_shrine_even_when_pack_is_adjacent()
        => Assert.False(ShrinePriorityPolicy.ShouldPreempt(
            exclusiveMechanicOwnsControl: true,
            hasEngagedTarget: true,
            shrineDistanceToPlayer: 10f,
            shrineDistanceToTarget: 10f));
}
