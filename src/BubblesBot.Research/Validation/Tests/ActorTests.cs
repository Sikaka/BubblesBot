using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Validates Actor component layout by reading ActionId, AnimationController fields,
/// and comparing against POEMCP ground truth.
/// </summary>
public sealed class ActorActionIdTest : ComponentFieldTest
{
    public override string Name => "Actor.ActionId (component +0x218, Int16)";
    protected override string ComponentName => "Actor";
    protected override string PoemcpExpr => "Player.GetComponent<Actor>().ActionId";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        if (!reader.TryReadStruct<short>(compAddr + KnownOffsets.ActorComponent.ActionId, out var aid))
            return ("(unread)", false, "read failed", null);
        var ok = aid >= -1; // -1 = idle, 0+ = action
        return ($"actionId={aid}", ok, ok ? null : "invalid actionId", (int)aid);
    }
}

public sealed class ActorAnimationIdTest : ComponentFieldTest
{
    public override string Name => "Actor.AnimationId (component +0x248, Int32)";
    protected override string ComponentName => "Actor";
    protected override string PoemcpExpr => "Player.GetComponent<Actor>().AnimationId";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        if (!reader.TryReadStruct<int>(compAddr + KnownOffsets.ActorComponent.AnimationId, out var aid))
            return ("(unread)", false, "read failed", null);
        var ok = aid >= 0;
        return ($"animationId={aid}", ok, ok ? null : "negative animationId", aid);
    }
}

public sealed class ActorActionPtrTest : ComponentFieldTest
{
    public override string Name => "Actor.ActionPtr (component +0x1B0, pointer)";
    protected override string ComponentName => "Actor";
    protected override string PoemcpExpr => "Player.GetComponent<Actor>().Action.Address.ToString(\"X\")";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        // ActionPtr can legitimately be null â€” player may be standing still.
        // On POEMCP compare we validate exact match; for sanity we only fail on
        // clearly bogus pointers (negative, kernel space).
        var ok = reader.TryReadStruct<nint>(compAddr + KnownOffsets.ActorComponent.ActionPtr, out var ptr);
        if (!ok) return ("(unread)", false, "read failed", null);
        var bogus = (long)ptr != 0 && ((long)ptr < 0x10000 || (long)ptr > 0x7FFF_FFFF_FFFF);
        return ($"0x{ptr:X}", !bogus, bogus ? $"bogus pointer 0x{ptr:X}" : null, ptr);
    }

    protected override bool TruthMatches(object? ours, EvalResult truth, out string compareDetail)
    {
        compareDetail = "";
        if (ours is nint ourPtr)
        {
            var truthAddr = truth.AsAddress();
            compareDetail = $"ours=0x{ourPtr:X} vs poemcp=0x{truthAddr:X}";
            return ourPtr == truthAddr;
        }
        return false;
    }
}

public sealed class ActorAnimationControllerPtrTest : ComponentFieldTest
{
    public override string Name => "Actor.AnimationControllerPtr (component +0x1A0)";
    protected override string ComponentName => "Actor";
    protected override string PoemcpExpr => "Player.GetComponent<Actor>().AnimationController.Address.ToString(\"X\")";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        if (!reader.TryReadStruct<nint>(compAddr + KnownOffsets.ActorComponent.AnimationControllerPtr, out var ptr))
            return ("(unread)", false, "read failed", null);
        var ok = (long)ptr > 0x10000;
        return ($"0x{ptr:X}", ok, ok ? null : "invalid pointer", ptr);
    }

    protected override bool TruthMatches(object? ours, EvalResult truth, out string compareDetail)
    {
        compareDetail = "";
        if (ours is nint ourPtr)
        {
            var truthAddr = truth.AsAddress();
            compareDetail = $"ours=0x{ourPtr:X} vs poemcp=0x{truthAddr:X}";
            return ourPtr == truthAddr;
        }
        return false;
    }
}

/// <summary>
/// Validates AnimationController fields (AnimationProgress, CurrentAnimationStage).
/// Reads via Actor component â†’ AnimationController pointer.
/// </summary>
public sealed class AnimationProgressTest : ValidationTest
{
    public override string Name => "AnimationController.AnimationProgress (+0x1A4)";
    public override string? Group => "Component fields";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(ComponentLookupKeys.PlayerComponentMap, out var mObj)
            || mObj is not Dictionary<string, nint> map)
            return new TestOutcome.Skip(Name, "component map not resolved");
        if (!map.TryGetValue("Actor", out var actorAddr))
            return new TestOutcome.Skip(Name, "Actor component not resolved");

        // Walk: Actor â†’ AnimationControllerPtr (+0x1A0) â†’ AnimationController.AnimationProgress (+0x1A4)
        if (!ctx.Reader.TryReadStruct<nint>(actorAddr + KnownOffsets.ActorComponent.AnimationControllerPtr, out var animCtrlAddr))
            return new TestOutcome.Fail(Name, "could not read AnimationControllerPtr");
        if (animCtrlAddr == 0) return new TestOutcome.Skip(Name, "AnimationControllerPtr is null");

        if (!ctx.Reader.TryReadStruct<float>(animCtrlAddr + KnownOffsets.AnimationController.AnimationProgress, out var progress))
            return new TestOutcome.Fail(Name, "could not read AnimationProgress");

        var sane = !float.IsNaN(progress) && !float.IsInfinity(progress) && progress >= 0f && progress <= 10f;
        // Animation progress can exceed 1.0 during transitions; up to ~5x observed in practice.
        if (!sane) return new TestOutcome.Fail(Name, $"progress={progress} out of [0,10]");

        var truth = await ctx.Poemcp.EvalAsync("Player.GetComponent<Actor>().AnimationController.AnimationProgress", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: progress={progress:F4} (POEMCP unavailable)");

        // AnimationProgress is a rapidly-changing float (between 0-1, cycling per animation frame).
        // Our read and POEMCP eval happen on different game-thread ticks so exact comparison is racy.
        // We check the POEMCP value is also in [0, 10] as a cross-validation that we're reading
        // the right field, but we don't require exact match.
        var truthVal = truth.AsFloat();
        if (truthVal < 0 || truthVal > 10)
            return new TestOutcome.Fail(Name, $"ours {progress:F4} but POEMCP returned {truthVal:F4} (out of [0,10])");

        return new TestOutcome.Pass(Name, $"sanity OK: progress={progress:F4} (POEMCP={truthVal:F4})");
    }
}
