using BubblesBot.Bot.Strategies;

namespace BubblesBot.Bot.Mechanics;

/// <summary>
/// The phases of the in-map objective scheduler. Currently produced as telemetry
/// (<c>MapRunPhase</c> mirrors the coarse hideout/clear/exit split); the full phase-owned
/// scheduler that consumes these is a deferred, live-validated step.
/// </summary>
public enum MapPhase { BossHunt, MainSweep, MechanicFinalization, RitualChain, Shop, FinalLoot, Exit }

/// <summary>
/// The closed, in-repo registry of which mechanic ids the runtime can actually execute today.
/// This is the single source of truth that strategy validation gates enabled blocks against —
/// there is deliberately no external/plugin registration. Adding a mechanic means adding its
/// block type, its runtime handling, and its id here, in-tree.
///
/// <para>This is the seam the full <c>IMechanicAdapter</c> extraction (deferred) will build on:
/// today it lists supported ids; later each id maps to a registered adapter factory.</para>
/// </summary>
public static class MechanicCatalog
{
    /// <summary>Mechanic <c>type</c> ids the in-map controller executes in this build.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        ShrinesBlock.TypeId,
        EldritchAltarsBlock.TypeId,
        RitualBlock.TypeId,
        MemoryTearsBlock.TypeId,
        DeliriumBlock.TypeId,
        UltimatumBlock.TypeId,
    };

    public static bool IsSupported(string mechanicId) => Supported.Contains(mechanicId);
}
