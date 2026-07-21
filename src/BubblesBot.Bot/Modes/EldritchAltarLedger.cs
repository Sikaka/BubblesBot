namespace BubblesBot.Bot.Modes;

/// <summary>
/// Session memory of Eldritch altars the bot has consumed or written off, keyed by
/// (areaHash, entityId).
///
/// <para><b>Why this exists.</b> A consumed TangleAltar keeps <c>Targetable=true</c> in
/// memory (observed live 2026-07-15 — no status transition for 14s after a confirmed
/// take), so <see cref="Core.Snapshot.MechanicsView"/>'s targetability fallback reports it
/// Available forever. Until the altar StateMachine contract is cracked, the bot's own
/// record is the only "used" signal. It cannot live inside the behavior: composers
/// <c>Reset()</c> interrupted/deselected children every tick, wiping any per-instance
/// memory (the live symptom was an endless re-target → 4s unreadable-wait → skip loop at
/// the consumed altar).</para>
///
/// <para>Static for the same reason as <c>EventLog</c>: the writer (take-altar behavior)
/// and the readers (overlay dots, /api/status, mode telemetry) live in unrelated object
/// graphs. Area-hash keying makes entries self-invalidating on map change; the size cap
/// is session hygiene, not correctness.</para>
/// </summary>
public static class EldritchAltarLedger
{
    private const int MaxEntries = 512;
    private static readonly object Gate = new();
    private static readonly Dictionary<(uint Area, uint Id), bool> Entries = new();

    /// <summary>Record an altar as handled. <paramref name="taken"/>: clicked and consumed
    /// (true) vs skipped by policy / unreadable / given up (false).</summary>
    public static void Mark(uint areaHash, uint entityId, bool taken)
    {
        if (areaHash == 0) return; // area read failed — don't poison a real map's key space
        lock (Gate)
        {
            if (Entries.Count >= MaxEntries) Entries.Clear();
            Entries[(areaHash, entityId)] = taken;
        }
    }

    public static bool IsResolved(uint areaHash, uint entityId)
    {
        lock (Gate) return Entries.ContainsKey((areaHash, entityId));
    }

    public static int CountTaken(uint areaHash)
    {
        lock (Gate)
        {
            var count = 0;
            foreach (var (key, taken) in Entries)
                if (key.Area == areaHash && taken) count++;
            return count;
        }
    }
}
