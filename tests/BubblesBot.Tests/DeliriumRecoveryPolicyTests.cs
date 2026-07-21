using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class DeliriumRecoveryPolicyTests
{
    [Fact]
    public void FreshlyCrossedNearbyMirrorIsNotMisclassifiedAsOldEncounter()
    {
        Assert.False(DeliriumController.ShouldTreatCompletedMirrorAsRestartRecovery(
            controllerWasAbsent: true, distanceFromMirror: 25));
    }

    [Fact]
    public void CompletedMirrorFarBehindPlayerCanRecoverAfterRestart()
    {
        Assert.True(DeliriumController.ShouldTreatCompletedMirrorAsRestartRecovery(
            controllerWasAbsent: true, distanceFromMirror: 500));
        Assert.False(DeliriumController.ShouldTreatCompletedMirrorAsRestartRecovery(
            controllerWasAbsent: false, distanceFromMirror: 500));
    }

    [Fact]
    public void NearbyCompletedMirrorGetsLongGraceForEndButtonBeforeColdAttachRecovery()
    {
        Assert.False(DeliriumController.ShouldSettleMissingEndButtonAfterColdAttach(
            farRestartRecovery: false, completedOnFirstObservation: true, elapsedSeconds: 5));
        Assert.True(DeliriumController.ShouldSettleMissingEndButtonAfterColdAttach(
            farRestartRecovery: false, completedOnFirstObservation: true, elapsedSeconds: 10));
        Assert.False(DeliriumController.ShouldSettleMissingEndButtonAfterColdAttach(
            farRestartRecovery: false, completedOnFirstObservation: false, elapsedSeconds: 30));
    }
}
