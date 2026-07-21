using BubblesBot.Bot.Input;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Reversible vendor-page research. It clicks only controls independently identified by their
/// visible -N- labels, proves that a different offer set loaded, and returns to the exact baseline
/// offer-set fingerprint. It never clicks an item offer.
/// </summary>
public sealed class VendorPageRoundTripLiveTest : ILiveTestCase
{
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow" };

    public string Id => "U-03-vendor-page-roundtrip";
    public string Name => "Vendor page/tab round trip";
    public string Description => "Uses production UI clicks to identify a different vendor page, validate its offers, and restore the original page.";
    public string ManualSetup => "Open a normal NPC purchase shop that has at least two -N- page controls and leave PoE focused. Do not hold an item on the cursor.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var baselineSnapshot = context.Snapshot();
        var baseline = VendorPurchaseView.Read(
            baselineSnapshot.Reader, baselineSnapshot.IngameStateAddress);
        context.Check(baseline.IsOpen, "purchase window baseline", $"panel=0x{(long)baseline.Panel:X}");
        context.Check(baseline.PageControls.Count >= 2, "vendor page controls", DescribePages(baseline));
        var baselineVisible = VisibleOffers(baseline);
        context.Check(baselineVisible.Count > 0, "baseline visible offers",
            $"visible={baselineVisible.Count} allocated={baseline.Offers.Count}");
        if (!baseline.IsOpen || baseline.PageControls.Count < 2 || baselineVisible.Count == 0)
            return LiveTestCaseResult.Blocked("the prepared shop does not expose multiple readable pages", "PageSetupMissing");

        var baselineFingerprint = ContentFingerprint(baselineVisible);
        var window = baselineSnapshot.Window;
        VendorPurchaseView.PageControl? changedControl = null;

        try
        {
            foreach (var page in baseline.PageControls.OrderBy(x => x.Page))
            {
                if (page.Rect is not { } rect || !rect.IntersectsWindow(window.Width, window.Height))
                    continue;

                var before = Read(context);
                var beforeVisible = VisibleOffers(before);
                var beforeFingerprint = ContentFingerprint(beforeVisible);
                var clicked = await ClickPageAsync(context, page, window, cancellationToken);
                if (!clicked)
                    return LiveTestCaseResult.Fail($"page {page.Page} could not be clicked through the guarded input path", "PageClickFailed");

                await Task.Delay(450, cancellationToken);
                var after = Read(context);
                var afterVisible = VisibleOffers(after);
                context.Check(after.IsOpen, $"page {page.Page} kept purchase window open",
                    $"panel=0x{(long)after.Panel:X}");
                context.Check(afterVisible.Count > 0, $"page {page.Page} visible offers readable",
                    $"visible={afterVisible.Count} allocated={after.Offers.Count}");

                var afterFingerprint = ContentFingerprint(afterVisible);
                if (!string.Equals(afterFingerprint, beforeFingerprint, StringComparison.Ordinal))
                {
                    changedControl = page;
                    context.Check(true, "alternate vendor page loaded",
                        $"control={page.Page} visibleOffers={beforeVisible.Count}->{afterVisible.Count}");
                    ValidateOffers(context, afterVisible, "alternate visible");
                    break;
                }

                context.Observe("already-selected vendor page",
                    $"control={page.Page} retained the baseline offer fingerprint");
            }

            if (changedControl is null)
                return LiveTestCaseResult.Fail("every readable page control retained one offer fingerprint", "PageHadNoEffect");

            var restored = await RestoreFingerprintAsync(
                context, window, baselineFingerprint, changedControl.Page, cancellationToken);
            if (!restored)
                return LiveTestCaseResult.Fail("an alternate vendor page loaded but the original page could not be restored", "RestoreFailed");

            var final = Read(context);
            var finalVisible = VisibleOffers(final);
            context.Check(final.IsOpen, "purchase window remained open", $"panel=0x{(long)final.Panel:X}");
            context.Check(
                string.Equals(ContentFingerprint(finalVisible), baselineFingerprint, StringComparison.Ordinal),
                "baseline vendor page restored",
                $"visible={finalVisible.Count} allocated={final.Offers.Count}");
            ValidateOffers(context, finalVisible, "restored visible");
            return LiveTestCaseResult.Pass(
                "a different vendor page loaded and the original offer set was exactly restored",
                "CompletedAndRestored");
        }
        finally
        {
            var current = Read(context);
            if (current.IsOpen
                && !string.Equals(ContentFingerprint(VisibleOffers(current)), baselineFingerprint, StringComparison.Ordinal))
            {
                context.Observe("failure recovery", "attempting bounded vendor-page baseline restore");
                _ = await RestoreFingerprintAsync(
                    context, window, baselineFingerprint, excludedPage: null, cancellationToken);
            }
            context.CancelAllInput();
        }
    }

    private static async Task<bool> RestoreFingerprintAsync(
        LiveTestContext context,
        WindowInfo window,
        string baselineFingerprint,
        int? excludedPage,
        CancellationToken cancellationToken)
    {
        var pages = Read(context).PageControls
            .Where(x => x.Page != excludedPage)
            .OrderBy(x => x.Page)
            .ToArray();
        foreach (var page in pages)
        {
            if (page.Rect is not { } rect || !rect.IntersectsWindow(window.Width, window.Height))
                continue;
            if (!await ClickPageAsync(context, page, window, cancellationToken))
                continue;
            await Task.Delay(450, cancellationToken);
            var current = Read(context);
            if (current.IsOpen
                && string.Equals(ContentFingerprint(VisibleOffers(current)), baselineFingerprint, StringComparison.Ordinal))
            {
                context.Check(true, "vendor page restore target", $"control={page.Page}");
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> ClickPageAsync(
        LiveTestContext context,
        VendorPurchaseView.PageControl page,
        WindowInfo window,
        CancellationToken cancellationToken)
    {
        if (page.Rect is not { } rect) return false;
        var point = AbsoluteCenter(window, rect);
        await context.HoverAsync(point.X, point.Y, 150, cancellationToken);
        var live = Read(context);
        var livePage = live.PageControls.SingleOrDefault(x => x.Page == page.Page);
        var clientX = point.X - window.OriginX;
        var clientY = point.Y - window.OriginY;
        var exactTarget = livePage is { Rect: { } liveRect }
            && livePage.Element == page.Element
            && string.Equals(livePage.Text, $"-{page.Page}-", StringComparison.Ordinal)
            && liveRect.Contains(clientX, clientY);
        context.Check(exactTarget, $"page {page.Page} target identity",
            $"element=0x{(long)page.Element:X} text='{page.Text}' point={point.X},{point.Y}");
        var outsideOffers = live.Offers.All(x => x.Rect is not { } offerRect
            || !offerRect.Contains(clientX, clientY));
        context.Check(outsideOffers, $"page {page.Page} target excludes item offers",
            $"clientPoint={clientX},{clientY} offers={live.Offers.Count}");
        if (!exactTarget || !outsideOffers) return false;

        var outcome = await context.VerifiedClickAsync(
            point.X,
            point.Y,
            ClickIntent.InteractUi,
            $"select vendor page {page.Page}",
            () =>
            {
                var current = Read(context);
                return current.IsOpen && current.PageControls.Any(x =>
                    x.Page == page.Page
                    && x.Element == page.Element
                    && string.Equals(x.Text, $"-{page.Page}-", StringComparison.Ordinal));
            },
            timeoutMs: 1_500,
            cancellationToken);
        return outcome == ActionOutcome.Confirmed;
    }

    private static VendorPurchaseView Read(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static void ValidateOffers(
        LiveTestContext context,
        IReadOnlyList<VendorPurchaseView.Offer> offers,
        string label)
    {
        context.Check(offers.All(x => x.Item != 0 && !string.IsNullOrWhiteSpace(x.Metadata)),
            $"{label} offer identities", $"count={offers.Count}");
        context.Check(offers.All(x => x.Rect is { Width: > 0, Height: > 0 }),
            $"{label} offer geometry", $"count={offers.Count}");
        context.Check(offers.Select(x => x.TreePath).Distinct(StringComparer.Ordinal).Count() == offers.Count,
            $"{label} offer paths unique", $"count={offers.Count}");
    }

    private static string ContentFingerprint(IReadOnlyList<VendorPurchaseView.Offer> offers)
        => string.Join('|', offers
            .OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{x.Metadata}:{x.BaseName}:"
                + $"{x.RequiredAttributes.Strength},{x.RequiredAttributes.Dexterity},{x.RequiredAttributes.Intelligence}"));

    private static IReadOnlyList<VendorPurchaseView.Offer> VisibleOffers(VendorPurchaseView view)
        => view.Offers.Where(x => x.IsVisible).ToArray();

    private static string DescribePages(VendorPurchaseView view)
        => string.Join(", ", view.PageControls.Select(x =>
            $"{x.Page}@0x{(long)x.Element:X}:{x.TextColor.B},{x.TextColor.G},{x.TextColor.R},{x.TextColor.A}/h{x.HighlightState}"));

    private static (int X, int Y) AbsoluteCenter(WindowInfo window, ElementGeometry.Rect rect)
        => (window.OriginX + (int)MathF.Round(rect.CenterX),
            window.OriginY + (int)MathF.Round(rect.CenterY));
}
