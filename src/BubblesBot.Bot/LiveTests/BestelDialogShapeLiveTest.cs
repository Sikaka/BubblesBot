using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Validate Bestel as a second NPC/dialog shape and restore the prepared Nessa shop.</summary>
public sealed class BestelDialogShapeLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private const string BestelPath = "Metadata/NPC/Act1/Bestel";
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow", "NpcDialog" };

    public string Id => "U-01-bestel-dialog-shape";
    public string Name => "Bestel second NPC/dialog shape";
    public string Description => "Closes the prepared Nessa shop, opens Bestel by exact world label, validates his non-vendor dialog and Goodbye continuation, then restores Nessa page 2.";
    public string ManualSetup => "In Lioneye's Watch, stand within label range of Nessa and Bestel with Nessa Purchase Items open on page 2. Hold no item and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var baseline = ReadShop(context);
        var baselineFingerprint = Fingerprint(baseline);
        context.Check(baseline.IsOpen, "prepared Nessa shop", $"visible={baseline.Offers.Count(x => x.IsVisible)}");
        if (!baseline.IsOpen)
            return LiveTestCaseResult.Blocked("Nessa Purchase Items is not open", "ShopSetupMissing");

        var closeShop = await context.VerifiedTapKeyAsync(
            EscapeVk, ClickIntent.InteractUi, "close Nessa shop for Bestel research",
            () => !ReadShop(context).IsOpen, timeoutMs: 2_000, cancellationToken);
        if (closeShop != ActionOutcome.Confirmed) return LiveTestCaseResult.Fail("Nessa shop did not close", "ShopCloseFailed");
        if (!await context.WaitForInputIdleAsync("after Nessa shop close", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");

        if (ReadDialog(context).IsOpen)
        {
            var closeDialog = await context.VerifiedTapKeyAsync(
                EscapeVk, ClickIntent.InteractUi, "close returned Nessa dialog",
                () => !ReadDialog(context).IsOpen, timeoutMs: 2_000, cancellationToken);
            if (closeDialog != ActionOutcome.Confirmed) return LiveTestCaseResult.Fail("Nessa dialog did not close", "NpcDialogCloseFailed");
            if (!await context.WaitForInputIdleAsync("after Nessa dialog close", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");
        }

        var snapshot = context.Snapshot();
        var bestel = snapshot.GroundLabels.SingleOrDefault(x =>
            x.Path == BestelPath
            && string.Equals(x.RenderName, "Bestel", StringComparison.Ordinal)
            && x.IsLabelVisible
            && x.IsRectOnScreen);
        if (bestel?.LabelRect is not { } rect)
            return LiveTestCaseResult.Fail("Bestel's exact visible world label was not readable", "NpcLabelMissing");
        context.Check(bestel.DistanceToPlayer < 60, "Bestel world identity",
            $"path='{bestel.Path}' name='{bestel.RenderName}' distance={bestel.DistanceToPlayer:F0}");

        var occluders = snapshot.GroundLabels
            .Where(x => x.LabelAddress != bestel.LabelAddress && x.IsLabelVisible)
            .Select(x => x.LabelRect)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();
        var uncovered = InteractSystem.FindUncoveredPoint(rect, occluders);
        context.Check(uncovered is not null, "Bestel label click point",
            uncovered is { } p ? $"client={p.X:F0},{p.Y:F0} occluders={occluders.Length}" : "fully occluded");
        if (uncovered is not { } point)
            return LiveTestCaseResult.Fail("Bestel label is fully occluded", "NpcLabelOccluded");

        var screen = snapshot.Window.ToScreen(point.X, point.Y);
        var openBestel = await context.VerifiedClickAsync(
            screen.X, screen.Y, ClickIntent.InteractWorld, "open Bestel dialog",
            () => ReadDialog(context).IsOpen, timeoutMs: 3_000, cancellationToken);
        if (openBestel != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Bestel label click did not open NpcDialog", "NpcDialogOpenFailed");
        if (!await context.WaitForInputIdleAsync("after Bestel click", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");

        var dialog = ReadDialog(context);
        var textSet = dialog.Controls.Select(x => x.Text.Trim())
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        context.Check(dialog.FindExact("Bestel").Count == 1, "Bestel dialog identity",
            $"panel=0x{(long)dialog.Panel:X}");
        context.Check(dialog.FindExact("Purchase Items").Count == 0,
            "Bestel dialog differs from Nessa vendor shape", "Purchase Items absent");
        context.Observe("Bestel dialog controls", string.Join(" | ", textSet.Take(40)));
        var goodbye = dialog.FindExact("Goodbye")
            .Where(x => x.Rect is { Width: > 0, Height: > 0 })
            .ToArray();
        context.Check(goodbye.Length == 1, "Bestel Goodbye identity", $"matches={goodbye.Length}");
        if (goodbye.Length != 1 || goodbye[0].Rect is not { } goodbyeRect)
            return LiveTestCaseResult.Fail("Bestel dialog did not expose one exact Goodbye control", "DialogOptionMismatch");

        var goodbyePoint = context.Snapshot().Window.ToScreen(goodbyeRect.CenterX, goodbyeRect.CenterY);
        var closeBestel = await context.VerifiedClickAsync(
            goodbyePoint.X, goodbyePoint.Y, ClickIntent.InteractUi, "select Bestel Goodbye",
            () => !ReadDialog(context).IsOpen, timeoutMs: 2_000, cancellationToken);
        if (closeBestel != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Bestel Goodbye did not close NpcDialog", "DialogContinuationMismatch");
        if (!await context.WaitForInputIdleAsync("after Bestel Goodbye", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");
        context.Check(!ReadDialog(context).IsOpen, "Bestel continuation", "NpcDialog closed");

        var restore = await new NpcShopRoundTripLiveTest().RunAsync(context, cancellationToken);
        if (restore.Outcome != LiveTestOutcome.Passed)
            return LiveTestCaseResult.Fail($"Bestel passed but Nessa shop restoration failed: {restore.Summary}", "RestoreFailed");
        var final = ReadShop(context);
        context.Check(Fingerprint(final) == baselineFingerprint,
            "prepared Nessa shop restored", $"visible={final.Offers.Count(x => x.IsVisible)}");
        if (Fingerprint(final) != baselineFingerprint)
            return LiveTestCaseResult.Fail("Nessa shop reopened on a different page/content fingerprint", "RestoreFailed");

        return LiveTestCaseResult.Pass(
            "Bestel world identity, distinct dialog shape, Goodbye continuation, and Nessa shop restoration all passed",
            "CompletedAndRestored");
    }

    private static NpcDialogView ReadDialog(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return NpcDialogView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static VendorPurchaseView ReadShop(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static string Fingerprint(VendorPurchaseView view)
        => string.Join('|', view.Offers.Where(x => x.IsVisible)
            .OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{x.Metadata}:{x.BaseName}:"
                + $"{x.RequiredAttributes.Strength},{x.RequiredAttributes.Dexterity},{x.RequiredAttributes.Intelligence}"));
}
