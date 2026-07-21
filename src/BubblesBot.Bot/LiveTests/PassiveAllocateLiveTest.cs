using BubblesBot.Bot.Input;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Allocate a passive node (checklist A-10). PoE passive allocation is <b>two-phase</b>: clicking a
/// node stages a PENDING change — the tree's "N Points Left" preview text drops — and an
/// <c>Apply Points</c> button commits it (the committed free count at
/// <see cref="KnownOffsets.ServerPlayerData.FreePassiveSkillPointsLeft"/> = 0x2D0 catches up), while
/// <c>Cancel</c> discards it. The preview count is read from the UI text (memory offset 0x360 was a
/// wrong guess — it did not track the preview); committed free is the 0x2D0 offset.
///
/// <para>The operator opens the tree and pans an allocatable node into view; its coordinate is captured
/// via POEMCP into <c>BBOT_PASSIVE_TARGET=clientX,clientY</c>. Default (reversible): click node → prove
/// the preview drops by one → Cancel → prove it restores, spending nothing. With <c>--commit</c>: click
/// node → Apply → prove committed free drops by one (the authoritative allocation), then refund the node
/// and Apply again to restore, spending one refund point.</para>
/// </summary>
public sealed class PassiveAllocateLiveTest : ILiveTestCase
{
    private const string ApplyText = "Apply Points";
    private const string CancelText = "Cancel";

    public string Id => "A-10-passive-allocate";
    public string Name => "Allocate a passive node (two-phase)";
    public string Description => "Clicks a passive node to stage a pending point, proves the 'Points Left' preview drops, then Cancels (reversible) or Applies + refunds (--commit) proving the committed count.";
    public string ManualSetup => "Open the passive tree (P) and pan an allocatable node into view. Pass BBOT_PASSIVE_TARGET=clientX,clientY (captured via POEMCP). Have >=1 free point (and >=1 refund point for --commit). Keep PoE focused; do not pan the tree.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible; // default cancels; --commit spends one refund point to restore
    public bool DrivesInput => true;

    private static bool TryParseTargetClient(out float x, out float y)
    {
        x = y = 0;
        var raw = Environment.GetEnvironmentVariable("BBOT_PASSIVE_TARGET");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out x)
            && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out y);
    }

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var start = context.Snapshot();
        context.Check(start.OpenPanels.IsOpen("TreePanel"), "passive tree open", $"open=[{string.Join(", ", start.OpenPanels.Open)}]");
        if (!start.OpenPanels.IsOpen("TreePanel"))
            return LiveTestCaseResult.Blocked("passive tree must be open (press P) before this test", "PassiveTreeClosed");

        var committed = Committed(context);
        var pointsLeft = PointsLeft(context);
        context.Check(pointsLeft is not null, "read 'N Points Left' preview from tree UI", $"pointsLeft={pointsLeft?.ToString() ?? "?"}");
        if (pointsLeft is null)
            return LiveTestCaseResult.Fail("could not read the tree 'Points Left' preview text", "PointsLeftUnreadable");

        // Baseline: discard any staged change so preview == committed.
        if (pointsLeft != committed)
        {
            if (!await ClickButtonAsync(context, CancelText, "discard pre-existing pending change",
                    () => PointsLeft(context) == committed, cancellationToken))
                return LiveTestCaseResult.Blocked("could not clear a pre-existing pending passive change", "PendingClearFailed");
            await context.WaitForInputIdleAsync("after baseline cancel", 1_200, cancellationToken);
            pointsLeft = PointsLeft(context);
        }

        context.Observe("counts BEFORE", $"committedFree(0x2D0)={committed} pointsLeftPreview={pointsLeft} refund={Read(context).PassiveRefundPointsLeft}");
        context.Check(pointsLeft == committed, "clean baseline (no staged change)", $"committed={committed} preview={pointsLeft}");
        context.Check(committed >= 1, "have a free point to spend", $"free={committed}");
        if (pointsLeft != committed || committed < 1)
            return LiveTestCaseResult.Blocked("need a clean baseline with >=1 free point", "BaselineNotReady");
        var refundBefore = Read(context).PassiveRefundPointsLeft;

        // Acquire the target node.
        var node = await AcquireNodeAsync(context, start, cancellationToken);
        if (node is not { } acq)
            return LiveTestCaseResult.Fail("no node-sized hovered element acquired", "NoHoveredNode");
        var (nodeRect, nodeAddr) = acq;
        var target = start.Window.ToScreen(nodeRect.CenterX, nodeRect.CenterY);
        context.Observe("allocation target", $"node=0x{(long)nodeAddr:X} client=({nodeRect.CenterX:F0},{nodeRect.CenterY:F0}) screen=({target.X},{target.Y})");

        // Stage: clicking the node drops the preview by one; committed is unchanged.
        var stage = await context.VerifiedClickAsync(
            target.X, target.Y,
            ClickIntent.InteractUi,
            "stage passive allocation (click node)",
            () => PointsLeft(context) == committed - 1,
            timeoutMs: 4_000,
            cancellationToken);
        if (stage != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("'Points Left' preview did not drop after clicking the node", "StageNoEffect");
        await context.WaitForInputIdleAsync("after stage", 1_200, cancellationToken);
        context.Check(PointsLeft(context) == committed - 1, "preview dropped by exactly 1", $"preview {committed} → {PointsLeft(context)}");
        context.Check(Committed(context) == committed, "committed unchanged while staged", $"committed={Committed(context)}");

        if (!context.Commit)
        {
            if (!await ClickButtonAsync(context, CancelText, "cancel staged allocation",
                    () => PointsLeft(context) == committed, cancellationToken))
                return LiveTestCaseResult.Fail("Cancel did not discard the staged allocation", "CancelFailed");
            await context.WaitForInputIdleAsync("after cancel", 1_200, cancellationToken);
            var revPreview = PointsLeft(context);
            var revCommitted = Committed(context);
            context.Check(revPreview == committed && revCommitted == committed,
                "staged allocation discarded, baseline restored", $"committed={revCommitted} preview={revPreview}");
            return revPreview == committed && revCommitted == committed
                ? LiveTestCaseResult.Pass($"staged a passive (preview {committed}→{committed - 1}) and cancelled; nothing spent", "AllocatedPendingThenCancelled")
                : LiveTestCaseResult.Fail("baseline not restored after cancel", "RestoreFailed");
        }

        // Commit: Apply lands the change on the committed count.
        if (!await ClickButtonAsync(context, ApplyText, "apply staged allocation",
                () => Committed(context) == committed - 1, cancellationToken))
            return LiveTestCaseResult.Fail("Apply did not commit the allocation", "ApplyFailed");
        await context.WaitForInputIdleAsync("after apply", 1_500, cancellationToken);
        var appliedCommitted = Committed(context);
        var appliedRefund = Read(context).PassiveRefundPointsLeft;
        context.Check(appliedCommitted == committed - 1, "committed free dropped by exactly 1", $"committed {committed} → {appliedCommitted}");
        context.Check(appliedRefund == refundBefore, "refund points unchanged by allocation", $"{refundBefore} → {appliedRefund}");

        // Restore: refund the node (right-click stages a refund), then Apply. Spends one refund point.
        var classification = "AllocatedAndCommitted";
        if (refundBefore >= 1)
        {
            var refundStage = await context.VerifiedRightClickAsync(
                target.X, target.Y,
                ClickIntent.InteractUi,
                "stage refund of the just-allocated node (right-click)",
                () => PointsLeft(context) == committed,
                timeoutMs: 4_000,
                cancellationToken);
            if (refundStage == ActionOutcome.Confirmed
                && await ClickButtonAsync(context, ApplyText, "apply the refund",
                    () => Committed(context) == committed, cancellationToken))
            {
                await context.WaitForInputIdleAsync("after refund apply", 1_500, cancellationToken);
                var ok = Committed(context) == committed && Read(context).PassiveRefundPointsLeft == refundBefore - 1;
                context.Check(ok, "node refunded (free restored, one refund point spent)",
                    $"committed {appliedCommitted}→{Committed(context)}, refund {refundBefore}→{Read(context).PassiveRefundPointsLeft}");
                if (ok) classification = "AllocatedCommittedAndRefunded";
            }
            else
            {
                context.Observe("refund not completed", "node left allocated (operator-authorized)");
            }
        }

        return appliedCommitted == committed - 1
            ? LiveTestCaseResult.Pass($"committed a passive allocation (free {committed}→{committed - 1}); {classification}", classification)
            : LiveTestCaseResult.Fail("allocation postconditions not fully met", "AllocatePostconditionFailed");
    }

    private static ServerPlayerInfoReader.Info Read(LiveTestContext context)
    {
        var s = context.Snapshot();
        return ServerPlayerInfoReader.Read(s.Reader, s.IngameDataAddress);
    }

    private static int Committed(LiveTestContext context) => Read(context).FreePassiveSkillPointsLeft;

    /// <summary>The live "N Points Left" preview from the tree UI (excludes "N Refund Points Left").</summary>
    private static int? PointsLeft(LiveTestContext context)
    {
        var s = context.Snapshot();
        var ui = VisibleUiTextView.ReadInGame(s.Reader, s.IngameStateAddress);
        var el = ui.FindContaining("Points Left")
            .FirstOrDefault(x => !x.Text.Contains("Refund", StringComparison.OrdinalIgnoreCase));
        if (el is null) return null;
        var digits = new string(el.Text.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    private static async Task<(ElementGeometry.Rect Rect, nint Addr)?> AcquireNodeAsync(
        LiveTestContext context, GameSnapshot start, CancellationToken cancellationToken)
    {
        UiHoverView hover = UiHoverView.Read(start.Reader, start.IngameStateAddress);
        var acquired = false;
        if (TryParseTargetClient(out var tx, out var ty))
        {
            var scr = start.Window.ToScreen(tx, ty);
            context.Observe("bot-hover target", $"client=({tx:F0},{ty:F0}) screen=({scr.X},{scr.Y})");
            for (var i = 0; i < 8 && !acquired; i++)
            {
                await context.HoverAsync(scr.X, scr.Y, 250, cancellationToken);
                var live = context.Snapshot();
                hover = UiHoverView.Read(live.Reader, live.IngameStateAddress);
                acquired = IsNode(hover, live.Window.Width, live.Window.Height);
            }
        }
        else
        {
            acquired = await context.WaitUntilAsync(
                "operator hovers a passive node",
                () =>
                {
                    var live = context.Snapshot();
                    hover = UiHoverView.Read(live.Reader, live.IngameStateAddress);
                    return IsNode(hover, live.Window.Width, live.Window.Height);
                },
                timeoutMs: 20_000,
                cancellationToken);
        }
        context.Check(acquired, "passive node acquired under cursor", acquired ? $"rect={hover.Rect?.Width:F0}x{hover.Rect?.Height:F0}" : "none");
        return acquired && hover.Rect is { } r ? (r, hover.Element) : null;
    }

    private static bool IsNode(UiHoverView hover, int w, int h)
        => hover.HasHover && hover.Rect is { Width: > 3 and < 60, Height: > 3 and < 60 } r && r.IntersectsWindow(w, h);

    private static async Task<bool> ClickButtonAsync(
        LiveTestContext context, string text, string description, Func<bool> postcondition, CancellationToken cancellationToken)
    {
        var snap = context.Snapshot();
        var ui = VisibleUiTextView.ReadInGame(snap.Reader, snap.IngameStateAddress);
        var btn = ui.FindExact(text)
            .FirstOrDefault(x => x.Rect is { } r && r.IntersectsWindow(snap.Window.Width, snap.Window.Height));
        if (btn?.Rect is not { } rect)
        {
            context.Check(false, $"locate '{text}' button", "not found on screen");
            return false;
        }
        context.Check(true, $"locate '{text}' button", $"element=0x{(long)btn.Element:X} rect=({rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0})");
        var p = snap.Window.ToScreen(rect.CenterX, rect.CenterY);
        var outcome = await context.VerifiedClickAsync(p.X, p.Y, ClickIntent.InteractUi, description, postcondition, 4_000, cancellationToken);
        return outcome == ActionOutcome.Confirmed;
    }
}
