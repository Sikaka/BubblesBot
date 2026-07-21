using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Player;

/// <summary>
/// Actor component: ActionId / AnimationId (both volatile, live-checked) plus the
/// AnimationController pointer and its AnimationProgress. Migrated from ActorTests.
/// </summary>
public sealed class PlayerActorProbe : IProbe
{
    public string Name => "player.actor";
    public string Group => "player";
    public string Description => "Actor ActionId/AnimationId live + AnimationController resolves.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var actor = ctx.Chain.PlayerComponent("Actor");
        if (actor == 0) return ProbeResult.Fail("no Actor component");

        var actionId = ctx.Reader.TryReadStruct<short>(actor + KnownOffsets.ActorComponent.ActionId, out var aid)
            ? Check.Live(ctx, "actor.actionid", aid, "Actor.ActionId", -1, short.MaxValue)
            : ProbeResult.Fail("Actor.ActionId unreadable");

        var animId = ctx.Reader.TryReadStruct<int>(actor + KnownOffsets.ActorComponent.AnimationId, out var anid)
            ? Check.Live(ctx, "actor.animationid", anid, "Actor.AnimationId", 0, 200_000)
            : ProbeResult.Fail("Actor.AnimationId unreadable");

        ProbeResult anim;
        if (ctx.Reader.TryReadStruct<nint>(actor + KnownOffsets.ActorComponent.AnimationControllerPtr, out var ac) && ac != 0)
            anim = ctx.Reader.TryReadStruct<float>(ac + KnownOffsets.AnimationController.AnimationProgress, out var p)
                   && float.IsFinite(p) && p is >= 0 and <= 10
                ? ProbeResult.Pass($"AnimationController@0x{(long)ac:X} progress={p:0.###}")
                : ProbeResult.Fail("AnimationController.AnimationProgress implausible");
        else
            anim = ProbeResult.Pass("AnimationControllerPtr null (idle)");

        return ProbeResult.Combine(actionId, animId, anim);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var actor = ctx.Chain.PlayerComponent("Actor");
        if (actor == 0) return ProbeResult.Found("Actor.AnimationId", []);
        return Discovery.IntValue(ctx, actor, "actor.animationid", 0x400, "Actor.AnimationId");
    }
}
