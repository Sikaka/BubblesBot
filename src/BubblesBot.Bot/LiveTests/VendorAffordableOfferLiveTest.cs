using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Find and validate an affordable offer from the live class-specific vendor page.</summary>
public sealed class VendorAffordableOfferLiveTest : ILiveTestCase
{
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow" };

    public string Id => "U-03-vendor-affordable-offer";
    public string Name => "Affordable vendor offer comparator";
    public string Description => "Discovers a class-specific visible offer with a structural non-red cost row, validates it, and restores the original hover without clicking.";
    public string ManualSetup => "Open a vendor page on a character that can afford at least one visible offer, leave no item held, and keep PoE focused. No particular class or gem is assumed.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var baseline = VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        var original = baseline.HoveredOffer;
        var visible = baseline.Offers
            .Where(x => x.IsVisible && x.Rect is { } rect
                && rect.IntersectsWindow(snapshot.Window.Width, snapshot.Window.Height))
            .OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .ToArray();
        context.Check(baseline.IsOpen, "purchase window baseline", $"panel=0x{(long)baseline.Panel:X}");
        context.Check(visible.Length > 0, "class-specific visible offers",
            $"visible={visible.Length} allocated={baseline.Offers.Count}");
        if (!baseline.IsOpen || visible.Length == 0)
            return LiveTestCaseResult.Blocked("the prepared shop has no visible offers", "OfferSetupMissing");

        var fingerprint = Fingerprint(baseline.Offers);
        VendorPurchaseView.Offer? affordable = null;
        var inspected = 0;
        foreach (var target in visible)
        {
            var point = AbsoluteCenter(snapshot.Window, target.Rect!.Value);
            await context.HoverAsync(point.X, point.Y, 100, cancellationToken);
            var reached = await context.WaitUntilAsync(
                $"affordability candidate {inspected + 1}/{visible.Length}",
                () => Read(context).HoveredOffer?.Element == target.Element,
                timeoutMs: 1_500,
                cancellationToken);
            if (!reached)
                return LiveTestCaseResult.Fail("candidate UIHover did not resolve exactly", "TooltipMismatch");
            var observed = Read(context).HoveredOffer;
            if (observed is null)
                return LiveTestCaseResult.Fail("candidate UIHover resolved without an offer", "TooltipMismatch");
            inspected++;
            if (observed.Costs.Count == 0 || IsUnavailable(observed.Costs)) continue;
            affordable = observed;
            break;
        }

        if (affordable is null)
            return LiveTestCaseResult.Fail(
                $"all {inspected} inspected visible offers had no cost or the unavailable red signature",
                "NoAffordableOffer");

        context.Check(affordable.IsVisible, "affordable offer remains visible",
            $"flags=0x{affordable.ElementFlags:X8}");
        context.Check(affordable.RenderedTooltip != 0
                && affordable.TooltipText.Contains(affordable.BaseName, StringComparison.OrdinalIgnoreCase),
            "affordable tooltip identity",
            $"name='{affordable.GeneratedName}' base='{affordable.BaseName}'");
        context.Check(affordable.Costs.Count > 0, "affordable structural price",
            FormatCosts(affordable.Costs));
        context.Check(!IsUnavailable(affordable.Costs), "affordable non-red color signature",
            FormatCosts(affordable.Costs));
        context.Observe("affordable comparator",
            $"inspected={inspected} name='{affordable.GeneratedName}' base='{affordable.BaseName}' "
            + $"flags=0x{affordable.ElementFlags:X8} {FormatCosts(affordable.Costs)}");

        await RestoreHoverAsync(context, snapshot.Window, original, baseline.PageControls, cancellationToken);
        var final = Read(context);
        context.Check(Fingerprint(final.Offers) == fingerprint,
            "vendor contents unchanged", $"offers={final.Offers.Count}");
        return LiveTestCaseResult.Pass(
            $"discovered and validated affordable offer '{affordable.GeneratedName}' after {inspected} candidate(s)",
            "CompletedAndRestored");
    }

    private static async Task RestoreHoverAsync(
        LiveTestContext context,
        WindowInfo window,
        VendorPurchaseView.Offer? original,
        IReadOnlyList<VendorPurchaseView.PageControl> pages,
        CancellationToken cancellationToken)
    {
        if (original?.Rect is { } originalRect)
        {
            var point = AbsoluteCenter(window, originalRect);
            await context.HoverAsync(point.X, point.Y, 120, cancellationToken);
            context.Check(Read(context).HoveredOffer?.Element == original.Element,
                "original offer hover restored", $"element=0x{(long)original.Element:X}");
            return;
        }
        var page = pages.FirstOrDefault(x => x.Rect is { } rect
            && rect.IntersectsWindow(window.Width, window.Height));
        if (page?.Rect is not { } pageRect)
        {
            context.Check(false, "unhighlighted state restore", "no safe page control was readable");
            return;
        }
        var safe = AbsoluteCenter(window, pageRect);
        await context.HoverAsync(safe.X, safe.Y, 120, cancellationToken);
        var snapshot = context.Snapshot();
        snapshot.Reader.TryReadStruct<nint>(
            snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
        context.Check(hover == 0, "unhighlighted state restored", $"UIHover=0x{(long)hover:X}");
    }

    private static bool IsUnavailable(IReadOnlyList<VendorPurchaseView.CostEntry> costs)
        => costs.Count > 0 && costs.All(x => IsUnavailableRed(x.CountTextColor)
            && IsUnavailableRed(x.CurrencyTextColor));

    private static bool IsUnavailableRed(ColorBGRA color)
        => color.A > 0 && color.R >= 180 && color.G <= 80 && color.B <= 80;

    private static string FormatCosts(IReadOnlyList<VendorPurchaseView.CostEntry> costs)
        => string.Join(" + ", costs.Select(x =>
            $"{x.Count}x {x.Currency} count={FormatColor(x.CountTextColor)} currency={FormatColor(x.CurrencyTextColor)}"));

    private static string FormatColor(ColorBGRA color)
        => $"BGRA({color.B},{color.G},{color.R},{color.A})";

    private static VendorPurchaseView Read(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static string Fingerprint(IReadOnlyList<VendorPurchaseView.Offer> offers)
        => string.Join('|', offers.OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{x.Metadata}:{x.BaseName}:{x.IsVisible}"));

    private static (int X, int Y) AbsoluteCenter(WindowInfo window, ElementGeometry.Rect rect)
        => (window.OriginX + (int)MathF.Round(rect.CenterX),
            window.OriginY + (int)MathF.Round(rect.CenterY));
}
