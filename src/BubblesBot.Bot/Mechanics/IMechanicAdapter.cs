using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Mechanics;

/// <summary>What kind of work a mechanic wants to do this tick, for the phase scheduler's arbitration.</summary>
public enum MechanicWorkKind { SweepInteraction, FinalizationVisit, ChainEncounter, ShopStep }

/// <summary>
/// One unit of mechanic work the scheduler can dispatch. <see cref="SweepBias"/> handicaps the
/// nearest-wins sweep arbitration (positive = treated as closer); <see cref="Behavior"/> is the
/// adapter-owned leaf that performs the interaction.
/// </summary>
public sealed record MechanicWork(MechanicWorkKind Kind, Vector2i Anchor, float SweepBias, IBehavior Behavior);

/// <summary>
/// The contract every built-in map mechanic will implement once the in-map controller is
/// refactored from its fixed Selector into an adapter-driven phase scheduler. It formalizes the
/// "Shared composer contracts" from MAP_FARMING_IMPLEMENTATION_PLAN.md: discovery
/// (<see cref="Observe"/>), per-phase work (<see cref="NextWork"/>), completion + reward
/// exhaustion (<see cref="IsSettled"/>), uniform evidence (<see cref="TelemetrySnapshot"/>), and
/// per-map / per-area lifecycle.
///
/// <para><b>Status:</b> the contract and the <see cref="MechanicCatalog"/> registry are shipped;
/// extracting the existing Shrine/Ritual/EldritchAltar behaviors into adapters and replacing
/// PushCombatMode's Selector with the scheduler is a deferred, live-validated step (it changes the
/// live-validated clear flow, so it must be proven on an armed map run before it is trusted).</para>
/// </summary>
public interface IMechanicAdapter
{
    /// <summary>Matches a mechanic block's <c>type</c> discriminator.</summary>
    string MechanicId { get; }

    /// <summary>Per-tick discovery: cache anchors, update the census. No interaction.</summary>
    void Observe(BehaviorContext ctx);

    /// <summary>The eligible work for the given phase, or null when this mechanic has nothing to do.</summary>
    MechanicWork? NextWork(BehaviorContext ctx, MapPhase phase);

    /// <summary>Completion evidence + reward exhaustion — the loot-barrier / phase-advance input.</summary>
    bool IsSettled(BehaviorContext ctx);

    /// <summary>Uniform evidence feed for telemetry / run reports.</summary>
    object TelemetrySnapshot();

    /// <summary>Per-map reset.</summary>
    void Reset();

    /// <summary>Detached-region lifecycle: sub-areas and boss arenas keep the parent run ledger.</summary>
    void OnAreaChanged(uint newAreaHash, AreaRole role);
}
