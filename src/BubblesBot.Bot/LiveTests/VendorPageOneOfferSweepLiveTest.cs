using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Select Nessa page 1, run the exhaustive visible-offer sweep, then restore page 2.</summary>
public sealed class VendorPageOneOfferSweepLiveTest : ILiveTestCase
{
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow" };

    public string Id => "U-03-vendor-page1-offer-sweep";
    public string Name => "Nessa page-1 exhaustive offer sweep";
    public string Description => "Selects page 1 through guarded UI input, validates every visible offer/price, then restores the prepared page-2 fingerprint.";
    public string ManualSetup => "Open Nessa Purchase Items on page 2, leave no item held or offer highlighted, and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var initial = Read(context);
        context.Check(initial.IsOpen && initial.PageControls.Any(x => x.Page == 1)
            && initial.PageControls.Any(x => x.Page == 2),
            "two-page Nessa baseline", $"pages=[{string.Join(",", initial.PageControls.Select(x => x.Page))}]");
        if (!initial.IsOpen || initial.PageControls.Count < 2)
            return LiveTestCaseResult.Blocked("the prepared shop does not expose Nessa pages 1 and 2", "PageSetupMissing");

        if (!await SelectPageAsync(context, 2, cancellationToken))
            return LiveTestCaseResult.Fail("page 2 could not be established as the restoration baseline", "PageClickFailed");
        var baseline = Read(context);
        var baselineFingerprint = Fingerprint(baseline);

        if (!await SelectPageAsync(context, 1, cancellationToken))
            return LiveTestCaseResult.Fail("page 1 could not be selected safely", "PageClickFailed");
        var pageOne = Read(context);
        context.Check(Fingerprint(pageOne) != baselineFingerprint,
            "page 1 offer fingerprint", $"visible={pageOne.Offers.Count(x => x.IsVisible)}");

        var sweep = await new VendorVisibleOfferSweepLiveTest().RunAsync(context, cancellationToken);
        if (sweep.Outcome != LiveTestOutcome.Passed)
        {
            _ = await SelectPageAsync(context, 2, cancellationToken);
            return sweep;
        }

        if (!await SelectPageAsync(context, 2, cancellationToken))
            return LiveTestCaseResult.Fail("page-1 sweep passed but page 2 could not be restored", "RestoreFailed");
        var final = Read(context);
        context.Check(Fingerprint(final) == baselineFingerprint,
            "prepared page 2 restored", $"visible={final.Offers.Count(x => x.IsVisible)}");
        if (Fingerprint(final) != baselineFingerprint)
            return LiveTestCaseResult.Fail("page 2 loaded without the baseline offer fingerprint", "RestoreFailed");
        return LiveTestCaseResult.Pass(
            "every visible page-1 offer was validated and the exact page-2 state restored",
            "CompletedAndRestored");
    }

    private static async Task<bool> SelectPageAsync(
        LiveTestContext context,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        var shop = Read(context);
        var page = shop.PageControls.SingleOrDefault(x => x.Page == pageNumber);
        var window = context.Snapshot().Window;
        if (page?.Rect is not { } rect || !rect.IntersectsWindow(window.Width, window.Height)) return false;
        var clientX = rect.CenterX;
        var clientY = rect.CenterY;
        if (shop.Offers.Any(x => x.Rect is { } offerRect && offerRect.Contains(clientX, clientY)))
            return false;
        var screen = window.ToScreen(clientX, clientY);
        await context.HoverAsync(screen.X, screen.Y, 120, cancellationToken);
        if (!await context.WaitForInputIdleAsync($"before page {pageNumber}", 1_500, cancellationToken))
            return false;
        var click = await context.VerifiedClickAsync(
            screen.X, screen.Y, ClickIntent.InteractUi, $"select vendor page {pageNumber}",
            () => Read(context).IsOpen, timeoutMs: 1_500, cancellationToken);
        if (click != ActionOutcome.Confirmed) return false;
        await Task.Delay(450, cancellationToken);
        return true;
    }

    private static VendorPurchaseView Read(LiveTestContext context)
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
