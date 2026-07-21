using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Snapshot view over an in-progress Ultimatum encounter. Surfaces:
/// <list type="bullet">
///   <item>The <b>spawner</b> (Trialmaster altar) — the entity the player clicks to start.
///         Path: <c>Metadata/Terrain/Leagues/Ultimatum/Objects/UltimatumChallengeInteractable</c>.</item>
///   <item>Any active <b>capture runes</b> — the "stand in circle" objective for the Trial
///         of Glory variant. Path: <c>Metadata/Terrain/Leagues/Ultimatum/Objects/CaptureRune</c>.</item>
/// </list>
///
/// <para>Detection is path-based via <see cref="EntityCache"/>. The view is rebuilt per
/// snapshot; it just filters the existing cache, no extra memory reads.</para>
///
/// <para><b>State-machine reads.</b> The spawner has named states <c>encounter_started</c>,
/// <c>encounter_finished</c>, and the rune has <c>stage</c>. State indices need to be
/// validated against POEMCP the first time we have a live Ultimatum to scan; v1 falls back
/// to <see cref="EntityCache.Entry.IsTargetable"/> and panel visibility for higher-level
/// state decisions. See <see cref="UltimatumStates"/> for the (placeholder, unverified)
/// index map.</para>
/// </summary>
public sealed class UltimatumView
{
    private const string SpawnerPathPrefix = "Metadata/Terrain/Leagues/Ultimatum/Objects/UltimatumChallengeInteractable";
    private const string CaptureRunePathPrefix = "Metadata/Terrain/Leagues/Ultimatum/Objects/CaptureRune";

    private readonly MemoryReader _reader;

    public EntityCache.Entry? Spawner { get; }
    public IReadOnlyList<EntityCache.Entry> CaptureRunes { get; }

    public UltimatumView(EntityCache cache, MemoryReader reader)
    {
        _reader = reader;
        EntityCache.Entry? spawner = null;
        var runes = new List<EntityCache.Entry>(0);

        foreach (var entry in cache.Entries.Values)
        {
            // Path-prefix match — PoE sometimes appends "@N" version tags to metadata paths
            // (Monsters/...@83, etc.). StartsWith covers both shapes.
            if (entry.Path.StartsWith(SpawnerPathPrefix, StringComparison.Ordinal))
            {
                // Prefer an active (still-stale-free) spawner. Multiple spawners in one zone
                // would be unusual but we pick the closest-to-player at call time anyway.
                if (spawner is null || (spawner.IsStale && !entry.IsStale)) spawner = entry;
            }
            else if (entry.Path.StartsWith(CaptureRunePathPrefix, StringComparison.Ordinal))
            {
                runes.Add(entry);
            }
        }

        Spawner = spawner;
        CaptureRunes = runes;
    }

    /// <summary>
    /// True iff a spawner is detected, still in PoE's live entity list (not stale), and not
    /// flagged "encounter finished" by the universal IsTargetable signal. Used by the mode
    /// to decide "is there an Ultimatum to do here."
    /// </summary>
    public bool HasActiveSpawner => Spawner is { IsStale: false, IsTargetable: true };

    /// <summary>
    /// Closest capture rune currently in stage=active. Trial of Glory shows multiple runes
    /// per round; only one is the active "stand here" target. Stage index is read directly
    /// from the state machine when available; otherwise the first IsTargetable rune wins.
    /// </summary>
    public EntityCache.Entry? ActiveCaptureRune(Vector2i fromGrid)
    {
        EntityCache.Entry? best = null;
        long bestD2 = long.MaxValue;
        foreach (var rune in CaptureRunes)
        {
            if (rune.IsStale) continue;
            if (!rune.IsTargetable) continue;
            // Cheaper than the state-machine read — if the named-state index of "stage" is
            // committed later, swap to: StateMachineView.ReadValue(rune.StateMachineCompAddr,
            // UltimatumStates.Rune.Stage) == 2.
            long dx = rune.GridPosition.X - fromGrid.X;
            long dy = rune.GridPosition.Y - fromGrid.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = rune; }
        }
        return best;
    }
}

/// <summary>
/// State-name → index mapping for Ultimatum entities. Indices are UNVERIFIED — wired
/// here so the bot is ready to swap to them once <c>--sweep-offsets</c> runs against a live
/// Ultimatum. Until then the mode falls back to <see cref="EntityCache.Entry.IsTargetable"/>
/// + panel-visibility checks, which work for the high-level Idle/Active/Done distinction.
/// </summary>
public static class UltimatumStates
{
    /// <summary>UltimatumChallengeInteractable (spawner / Trialmaster altar).</summary>
    public static class Spawner
    {
        // Live current-build captures (2026-07-20): fresh [0,0,0,1], active
        // [1,0,0,1], ten-wave success [1,1,0,0].
        public const int LiveEncounterStarted = 0;
        public const int LiveEncounterFinished = 1;
        public const int LiveReady = 3;
        public const int EncounterStarted  = -1;  // unverified — needs index probe vs POEMCP
        public const int EncounterFinished = -1;  // unverified — 0=active, 1=success, 2=failed
    }

    /// <summary>CaptureRune (Trial of Glory "stand here" target).</summary>
    public static class Rune
    {
        public const int Stage = -1;  // unverified — 2 = active
    }
}
