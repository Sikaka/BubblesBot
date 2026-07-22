namespace BubblesBot.Bot.Strategies;

public enum GuardianRotaObjectiveKind { GuardianMap, FormedInvitation, Finished }

public readonly record struct GuardianRotaObjective(
    GuardianRotaObjectiveKind Kind,
    string Name,
    int RotationNumber);

/// <summary>
/// Pure campaign progress for repeated Formed rotations. Atlas witness evidence decides the
/// next objective; successful map clears are not treated as witness proof until a later scan.
/// </summary>
public sealed class GuardianRotaProgress
{
    private readonly int _targetRotations;

    public GuardianRotaProgress(int targetRotations)
        => _targetRotations = Math.Max(1, targetRotations);

    public int RotationsCompleted { get; private set; }
    public int InvitationsCompleted { get; private set; }
    public int GuardianMapsCompleted { get; private set; }
    public int DeathsThisEncounter { get; private set; }
    public int PortalEntriesThisEncounter { get; private set; }

    public void RestoreTotals(int rotationsCompleted, int invitationsCompleted,
        int guardianMapsCompleted)
    {
        // Completing The Formed defines a completed rotation, so normalize the two durable
        // counters together. This also bounds corrupt or hand-edited local checkpoints.
        var completed = Math.Clamp(
            Math.Max(rotationsCompleted, invitationsCompleted), 0, _targetRotations);
        RotationsCompleted = completed;
        InvitationsCompleted = completed;
        GuardianMapsCompleted = Math.Max(0, guardianMapsCompleted);
    }

    public GuardianRotaObjective Decide(
        IReadOnlyDictionary<string, GuardianWitnessStatus> witnessStates)
    {
        if (RotationsCompleted >= _targetRotations)
            return new(GuardianRotaObjectiveKind.Finished, "Complete", RotationsCompleted);
        if (!GuardianRotationPolicy.TrySelectNext(witnessStates, out var next, out var ready))
            throw new InvalidOperationException("Guardian witness state is incomplete or unknown");
        return ready
            ? new(GuardianRotaObjectiveKind.FormedInvitation, "The Formed", RotationsCompleted + 1)
            : new(GuardianRotaObjectiveKind.GuardianMap, next!, RotationsCompleted + 1);
    }

    public void RecordGuardianClear()
    {
        GuardianMapsCompleted++;
        ResetEncounterBudget();
    }

    public void RecordInvitationClear()
    {
        InvitationsCompleted++;
        RotationsCompleted++;
        ResetEncounterBudget();
    }

    public bool RecordPortalEntry()
    {
        PortalEntriesThisEncounter++;
        return PortalEntriesThisEncounter <= 6;
    }

    public bool RecordDeath()
    {
        DeathsThisEncounter++;
        return DeathsThisEncounter <= 5 && PortalEntriesThisEncounter < 6;
    }

    public void ResetEncounterBudget()
    {
        DeathsThisEncounter = 0;
        PortalEntriesThisEncounter = 0;
    }
}
