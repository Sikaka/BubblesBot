using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Close a prepared Nessa shop, reopen it through the NPC/dialog, and restore the page.</summary>
public sealed class NpcShopRoundTripLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private const string NessaPath = "Metadata/NPC/Act1/Nessa";
    private const string PurchaseOption = "Purchase Items";
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow", "NpcDialog" };

    public string Id => "A-01-nessa-shop-roundtrip";
    public string Name => "Nessa shop close/reopen round trip";
    public string Description => "Closes Purchase Items, identifies Nessa and her exact dialog option, reopens the shop, and restores the original visible page.";
    public string ManualSetup => "Stand alive within label range of Nessa with Purchase Items open on page 2. A prior failed research step may leave the shop closed; the test can recover it. Hold no item and leave PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var baseline = ReadShop(context);
        if (!baseline.IsOpen)
        {
            context.Observe("prepared-state recovery", "Purchase Items is closed; reopening Nessa and selecting page 2 before the measured cycle");
            if (!await OpenShopAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail("could not recover the prepared Nessa shop", "SetupRecoveryFailed");
            if (!await SelectPageAsync(context, 2, cancellationToken))
                return LiveTestCaseResult.Fail("Nessa shop reopened but page 2 could not be prepared", "SetupRecoveryFailed");
            baseline = ReadShop(context);
        }

        var baselineVisible = VisibleOffers(baseline);
        context.Check(baseline.IsOpen, "purchase window baseline", $"panel=0x{(long)baseline.Panel:X}");
        context.Check(baselineVisible.Count > 0, "baseline visible offers", $"count={baselineVisible.Count}");
        var baselineFingerprint = Fingerprint(baselineVisible);
        if (!baseline.IsOpen || baselineVisible.Count == 0)
            return LiveTestCaseResult.Blocked("Purchase Items is not open with a readable offer page", "ShopSetupMissing");

        var close = await context.VerifiedTapKeyAsync(
            EscapeVk, ClickIntent.InteractUi, "close Nessa Purchase Items",
            () => !ReadShop(context).IsOpen, timeoutMs: 2_000, cancellationToken);
        if (close != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Escape did not close Purchase Items", "ShopCloseFailed");
        if (!await context.WaitForInputIdleAsync("after shop close", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input router did not settle after closing the shop", "InputSettleFailed");

        var afterClose = context.Snapshot();
        context.Check(!afterClose.OpenPanels.IsOpen("PurchaseWindow"),
            "purchase window closed", $"open=[{string.Join(", ", afterClose.OpenPanels.Open)}]");

        if (ReadDialog(context).IsOpen)
        {
            var closeDialog = await context.VerifiedTapKeyAsync(
                EscapeVk, ClickIntent.InteractUi, "close Nessa dialog before world-label reopen",
                () => !ReadDialog(context).IsOpen, timeoutMs: 2_000, cancellationToken);
            if (closeDialog != ActionOutcome.Confirmed)
                return LiveTestCaseResult.Fail("the returned Nessa dialog could not be closed", "NpcDialogCloseFailed");
            if (!await context.WaitForInputIdleAsync("after Nessa dialog close", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input router did not settle after closing Nessa dialog", "InputSettleFailed");
        }
        context.Check(!ReadDialog(context).IsOpen, "NPC dialog closed for world interaction",
            "world-label path is now required");

        if (!await OpenShopAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("Nessa and Purchase Items did not reopen the shop", "ShopReopenFailed");

        var reopened = ReadShop(context);
        context.Check(reopened.IsOpen, "purchase window reopened", $"panel=0x{(long)reopened.Panel:X}");
        context.Check(!ReadDialog(context).IsOpen, "NPC dialog continuation",
            "NpcDialog closed after Purchase Items opened");

        if (!string.Equals(Fingerprint(VisibleOffers(reopened)), baselineFingerprint, StringComparison.Ordinal))
        {
            var restored = await RestorePageAsync(context, baselineFingerprint, cancellationToken);
            if (!restored)
                return LiveTestCaseResult.Fail("shop reopened but the prepared page could not be restored", "RestoreFailed");
        }

        var final = ReadShop(context);
        var finalVisible = VisibleOffers(final);
        context.Check(string.Equals(Fingerprint(finalVisible), baselineFingerprint, StringComparison.Ordinal),
            "baseline shop page restored", $"visible={finalVisible.Count}");
        return LiveTestCaseResult.Pass(
            "Nessa shop closed, exact NPC/dialog identities reopened it, and the original page was restored",
            "CompletedAndRestored");
    }

    private static async Task<bool> OpenShopAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var dialog = ReadDialog(context);
        if (!dialog.IsOpen)
        {
            var snapshot = context.Snapshot();
            var nessa = FindNessa(snapshot);
            if (nessa is null || nessa.LabelRect is not { } rect)
            {
                context.Check(false, "Nessa world identity", "exact visible label was not readable");
                return false;
            }
            context.Check(nessa.Path == NessaPath && nessa.RenderName == "Nessa" && nessa.IsLabelVisible,
                "Nessa world identity",
                $"path='{nessa.Path}' name='{nessa.RenderName}' distance={nessa.DistanceToPlayer:F0}");

            var occluders = snapshot.GroundLabels
                .Where(x => x.LabelAddress != nessa.LabelAddress && x.IsLabelVisible)
                .Select(x => x.LabelRect)
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .ToArray();
            var uncovered = InteractSystem.FindUncoveredPoint(rect, occluders);
            context.Check(uncovered is not null, "Nessa label click point",
                uncovered is { } p ? $"client={p.X:F0},{p.Y:F0} occluders={occluders.Length}" : "fully occluded");
            if (uncovered is not { } point) return false;

            if (!await context.WaitForInputIdleAsync("before Nessa click", 1_500, cancellationToken))
                return false;
            var screen = snapshot.Window.ToScreen(point.X, point.Y);
            var clickNpc = await context.VerifiedClickAsync(
                screen.X, screen.Y, ClickIntent.InteractWorld, "open Nessa dialog",
                () => ReadDialog(context).IsOpen, timeoutMs: 3_000, cancellationToken);
            if (clickNpc != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync("after Nessa click", 1_500, cancellationToken))
                return false;
            dialog = ReadDialog(context);
        }

        context.Check(dialog.IsOpen, "Nessa dialog open", $"panel=0x{(long)dialog.Panel:X}");
        context.Observe("NPC dialog controls",
            string.Join(" | ", dialog.Controls.Select(x => $"'{OneLine(x.Text)}'@{x.TreePath}").Take(40)));
        var matches = dialog.FindExact(PurchaseOption)
            .Where(x => x.Rect is { Width: > 0, Height: > 0 })
            .ToArray();
        context.Check(matches.Length == 1, "Purchase Items option identity", $"matches={matches.Length}");
        if (matches.Length != 1 || matches[0].Rect is not { }) return false;

        var liveDialog = ReadDialog(context);
        var liveMatch = liveDialog.FindExact(PurchaseOption)
            .SingleOrDefault(x => x.Element == matches[0].Element);
        context.Check(liveMatch?.Rect is not null, "Purchase Items target stable",
            $"element=0x{(long)matches[0].Element:X} path={matches[0].TreePath}");
        if (liveMatch?.Rect is not { } liveRect) return false;

        if (!await context.WaitForInputIdleAsync("before Purchase Items click", 1_500, cancellationToken))
            return false;
        var optionPoint = context.Snapshot().Window.ToScreen(liveRect.CenterX, liveRect.CenterY);
        var open = await context.VerifiedClickAsync(
            optionPoint.X, optionPoint.Y, ClickIntent.InteractUi, "select Nessa Purchase Items",
            () => ReadShop(context).IsOpen, timeoutMs: 3_000, cancellationToken);
        if (open != ActionOutcome.Confirmed) return false;
        return await context.WaitForInputIdleAsync("after Purchase Items click", 1_500, cancellationToken);
    }

    private static async Task<bool> SelectPageAsync(
        LiveTestContext context,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        var shop = ReadShop(context);
        var page = shop.PageControls.SingleOrDefault(x => x.Page == pageNumber);
        var window = context.Snapshot().Window;
        if (page?.Rect is not { } rect || !rect.IntersectsWindow(window.Width, window.Height)) return false;
        var clientPoint = (X: rect.CenterX, Y: rect.CenterY);
        if (shop.Offers.Any(x => x.Rect is { } offerRect && offerRect.Contains(clientPoint.X, clientPoint.Y)))
            return false;
        if (!await context.WaitForInputIdleAsync($"before vendor page {pageNumber}", 1_500, cancellationToken))
            return false;
        var screen = window.ToScreen(clientPoint.X, clientPoint.Y);
        var click = await context.VerifiedClickAsync(
            screen.X, screen.Y, ClickIntent.InteractUi, $"select vendor page {pageNumber}",
            () => ReadShop(context).IsOpen, timeoutMs: 1_500, cancellationToken);
        if (click != ActionOutcome.Confirmed) return false;
        await Task.Delay(450, cancellationToken);
        return true;
    }

    private static async Task<bool> RestorePageAsync(
        LiveTestContext context,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var initial = ReadShop(context);
        foreach (var page in initial.PageControls.OrderBy(x => x.Page))
        {
            if (!await SelectPageAsync(context, page.Page, cancellationToken)) continue;
            if (string.Equals(Fingerprint(VisibleOffers(ReadShop(context))), fingerprint, StringComparison.Ordinal))
            {
                context.Check(true, "shop page restore target", $"control={page.Page}");
                return true;
            }
        }
        return false;
    }

    private static GroundLabelView? FindNessa(GameSnapshot snapshot)
        => snapshot.GroundLabels.SingleOrDefault(x =>
            x.Path == NessaPath
            && string.Equals(x.RenderName, "Nessa", StringComparison.Ordinal)
            && x.IsLabelVisible
            && x.IsRectOnScreen);

    private static VendorPurchaseView ReadShop(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static NpcDialogView ReadDialog(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return NpcDialogView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static IReadOnlyList<VendorPurchaseView.Offer> VisibleOffers(VendorPurchaseView view)
        => view.Offers.Where(x => x.IsVisible).ToArray();

    private static string Fingerprint(IReadOnlyList<VendorPurchaseView.Offer> offers)
        => string.Join('|', offers.OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{x.Metadata}:{x.BaseName}:"
                + $"{x.RequiredAttributes.Strength},{x.RequiredAttributes.Dexterity},{x.RequiredAttributes.Intelligence}"));

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', '|').Trim();
}
