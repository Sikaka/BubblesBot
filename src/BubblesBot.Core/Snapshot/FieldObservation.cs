namespace BubblesBot.Core.Snapshot;

public enum ObservationTruth { Unknown, False, True }
public enum ObservationReadStatus { NeverRead, Success, MissingComponent, ReadFailed, InvalidValue }
public enum ObservationConfidence { Experimental, Validated }

/// <summary>Provenance-bearing observation for safety-critical boolean memory semantics.</summary>
public readonly record struct BooleanObservation(
    ObservationTruth Truth,
    ObservationReadStatus ReadStatus,
    ObservationConfidence Confidence,
    string Source,
    int ObservedTick)
{
    public bool IsKnown => Truth is not ObservationTruth.Unknown;
    public bool IsTrue => Truth is ObservationTruth.True;

    public static BooleanObservation Unknown(
        string source,
        int tick,
        ObservationReadStatus status,
        ObservationConfidence confidence = ObservationConfidence.Experimental)
        => new(ObservationTruth.Unknown, status, confidence, source, tick);

    public static BooleanObservation Known(
        bool value,
        string source,
        int tick,
        ObservationConfidence confidence)
        => new(value ? ObservationTruth.True : ObservationTruth.False,
            ObservationReadStatus.Success, confidence, source, tick);
}

public readonly record struct LongObservation(
    long Value,
    bool IsKnown,
    ObservationReadStatus ReadStatus,
    ObservationConfidence Confidence,
    string Source,
    int ObservedTick)
{
    public static LongObservation Unknown(
        string source, int tick, ObservationReadStatus status,
        ObservationConfidence confidence = ObservationConfidence.Experimental)
        => new(0, false, status, confidence, source, tick);

    public static LongObservation Known(
        long value, string source, int tick,
        ObservationConfidence confidence = ObservationConfidence.Experimental)
        => new(value, true, ObservationReadStatus.Success, confidence, source, tick);
}
