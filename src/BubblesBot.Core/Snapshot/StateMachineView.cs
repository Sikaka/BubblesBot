using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Reads the int64 state-value array off a StateMachineComponent. Validated 2026-05-08:
/// the component stores a pointer at <c>+0x160</c> to a flat int64 array, one entry per
/// state defined for the entity in PoE's <c>StatesFile.dat</c>. State names live in the
/// data files (not memory), so callers must know the index→name mapping for the entity
/// type they're reading.
///
/// <para>Typical usage: <c>StateMachineView.ReadValue(reader, entry.StateMachineCompAddr,
/// index: 5)</c> to read e.g. <c>BlightPump.success</c>. For higher-level mechanic checks
/// see <see cref="MechanicsView"/>.</para>
/// </summary>
public static class StateMachineView
{
    public static LongObservation ObserveValue(
        MemoryReader reader, nint stateMachineCompAddr, int index, int observedTick,
        string? source = null)
    {
        source ??= $"StateMachine[{index}]";
        if (stateMachineCompAddr == 0)
            return LongObservation.Unknown(source, observedTick, ObservationReadStatus.MissingComponent);
        if (!reader.TryReadStruct<nint>(
                stateMachineCompAddr + KnownOffsets.StateMachineComponent.StatesPtr, out var arr)
            || arr == 0)
            return LongObservation.Unknown(source, observedTick, ObservationReadStatus.ReadFailed);
        return reader.TryReadStruct<long>(arr + index * 8, out var value)
            ? LongObservation.Known(value, source, observedTick, ObservationConfidence.Validated)
            : LongObservation.Unknown(source, observedTick, ObservationReadStatus.ReadFailed);
    }

    /// <summary>Read a single state value by index. Returns null on read failure.</summary>
    public static long? ReadValue(MemoryReader reader, nint stateMachineCompAddr, int index)
    {
        var observation = ObserveValue(reader, stateMachineCompAddr, index, 0);
        return observation.IsKnown ? observation.Value : null;
    }

    /// <summary>Read up to <paramref name="count"/> values starting at index 0. Out-of-range
    /// indices return 0 silently — caller knows the entity-specific count.</summary>
    public static long[] ReadValues(MemoryReader reader, nint stateMachineCompAddr, int count)
    {
        var result = new long[count];
        if (stateMachineCompAddr == 0) return result;
        if (!reader.TryReadStruct<nint>(stateMachineCompAddr + KnownOffsets.StateMachineComponent.StatesPtr, out var arr))
            return result;
        if (arr == 0) return result;
        for (int i = 0; i < count; i++)
        {
            if (reader.TryReadStruct<long>(arr + i * 8, out var v)) result[i] = v;
        }
        return result;
    }
}

/// <summary>
/// State-name → index mapping for entity types we care about. The order is fixed by PoE's
/// data files and stable across game versions (only changes when GGG adds new states, which
/// is rare). Validated 2026-05-08 against POEMCP.
/// </summary>
public static class BlightStates
{
    /// <summary>BlightPump state indices.</summary>
    public static class Pump
    {
        public const int ReadyToBuild  = 0;
        public const int Health        = 1;  // remaining lane segments / pump HP
        public const int UiDescription = 2;
        public const int NextPath      = 3;
        public const int Activated     = 4;  // > 0 once player clicks pump and encounter starts
        public const int Success       = 5;  // > 0 when encounter cleared
        public const int Fail          = 6;  // > 0 when pump destroyed
        public const int BuildStep     = 7;
        public const int ReadyToStart  = 8;  // 1 when pump is clickable to start

        public const int Count = 9;
    }
}

/// <summary>Live-validated RitualRuneInteractable state indices.</summary>
public static class RitualStates
{
    public static class RuneInteractable
    {
        // AutoExile names plus live 2026-07-14 fresh-altar capture [1,1].
        public const int CurrentState = 0;
        public const int InteractionEnabled = 1;
        public const int Count = 2;
    }
}

/// <summary>Live-validated Delirium mirror (<c>AfflictionInitiator</c>) state indices.</summary>
public static class DeliriumStates
{
    public static class Mirror
    {
        // POEMCP ordered-state capture, Strand 2026-07-20: [interacted].
        public const int Interacted = 0;
        public const int Count = 1;
    }
}

/// <summary>
/// Simulacrum monolith (<c>Metadata/MiscellaneousObjects/Afflictionator</c>) state contract.
/// AutoExile proves the semantic names (<c>active</c>, <c>goodbye</c>, <c>wave</c>), but
/// ExileCore resolves those names through game data while BubblesBot reads the flat value array.
/// Keep the indices uncommitted until one current-build before/during/after wave capture proves
/// their order. Consumers must check <see cref="IsValidated"/> and fail closed.
/// </summary>
public static class SimulacrumStates
{
    public static class Monolith
    {
        // Current-build ordered ExileCore capture plus direct-array diff, 2026-07-14:
        // [active, wave, hello, goodbye, play_interlude, affliction_endgame_initiator].
        public const int Active = 0;
        public const int Goodbye = 3;
        public const int Wave = 1;
        public const int CaptureCount = 6;

        public static bool IsValidated => Active >= 0 && Goodbye >= 0 && Wave >= 0;
    }
}
