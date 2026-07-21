using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Validate a declared unaffordable vendor offer, hover another offer, and restore it.</summary>
public sealed class VendorUnavailableOfferLiveTest : ILiveTestCase
{
    private const string ExpectedName = "Agile Iron Ring";
    private const string ExpectedBase = "Iron Ring";
    private const string ExpectedCurrency = "Orb of Alteration";
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow" };

    public string Id => "U-03-vendor-unavailable-offer";
    public string Name => "Unaffordable vendor offer round trip";
    public string Description => "Proves a visible offer has structural red/unaffordable cost rows, hovers a distinct offer, then restores the exact negative target.";
    public string ManualSetup => "On the prepared SSF character, open the vendor page and hover Agile Iron Ring showing a red 1x Orb of Alteration cost. Hold no item and keep PoE focused.";
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
        context.Check(baseline.IsOpen, "purchase window baseline", $"panel=0x{(long)baseline.Panel:X}");
        if (original?.Rect is not { } originalRect)
            return LiveTestCaseResult.Blocked("the prepared hover did not resolve one vendor offer", "HoverSetupMissing");
        CheckPreparedTarget(context, original, "original");
        if (!MatchesPreparedTarget(original) || !IsUnavailable(original.Costs))
            return LiveTestCaseResult.Fail("the hovered offer is not the declared unavailable Agile Iron Ring", "AvailabilityMismatch");

        var fingerprint = Fingerprint(baseline.Offers);
        var alternate = baseline.Offers
            .Where(x => x.IsVisible && x.Element != original.Element
                && x.Rect is { } rect && rect.IntersectsWindow(snapshot.Window.Width, snapshot.Window.Height))
            .OrderByDescending(x => DistanceSquared(x.Rect!.Value, originalRect))
            .FirstOrDefault();
        if (alternate?.Rect is not { } alternateRect)
            return LiveTestCaseResult.Blocked("no distinct visible vendor offer is available", "NoAlternateOffer");

        var alternatePoint = AbsoluteCenter(snapshot.Window, alternateRect);
        await context.HoverAsync(alternatePoint.X, alternatePoint.Y, 120, cancellationToken);
        var reachedAlternate = await context.WaitUntilAsync(
            "alternate availability hover",
            () => Read(context).HoveredOffer?.Element == alternate.Element,
            timeoutMs: 2_000,
            cancellationToken);
        if (!reachedAlternate)
            return LiveTestCaseResult.Fail("alternate offer hover did not resolve exactly", "TooltipMismatch");
        var observedAlternate = Read(context).HoveredOffer;
        if (observedAlternate is null)
            return LiveTestCaseResult.Fail("alternate UIHover resolved without an offer", "TooltipMismatch");
        context.Check(observedAlternate.Item == alternate.Item,
            "alternate item identity", $"base='{observedAlternate.BaseName}' item=0x{(long)observedAlternate.Item:X}");
        context.Check(observedAlternate.Costs.Count > 0,
            "alternate structural cost", FormatCosts(observedAlternate.Costs));
        context.Observe("alternate availability signature",
            $"classification={(IsUnavailable(observedAlternate.Costs) ? "unavailable" : "available/unknown")} "
            + $"flags=0x{observedAlternate.ElementFlags:X8} {FormatCosts(observedAlternate.Costs)}");

        var originalPoint = AbsoluteCenter(snapshot.Window, originalRect);
        await context.HoverAsync(originalPoint.X, originalPoint.Y, 120, cancellationToken);
        var restored = await context.WaitUntilAsync(
            "unavailable offer restore",
            () => Read(context).HoveredOffer?.Element == original.Element,
            timeoutMs: 2_000,
            cancellationToken);
        if (!restored)
            return LiveTestCaseResult.Fail("the exact unavailable offer hover was not restored", "RestoreFailed");
        var final = Read(context);
        if (final.HoveredOffer is { } restoredOffer) CheckPreparedTarget(context, restoredOffer, "restored");
        context.Check(Fingerprint(final.Offers) == fingerprint,
            "vendor contents unchanged", $"offers={final.Offers.Count}");
        return LiveTestCaseResult.Pass(
            "visible unavailable cost rows were identified, a distinct offer was inspected, and the exact negative target restored",
            "CompletedAndRestored");
    }

    private static void CheckPreparedTarget(
        LiveTestContext context,
        VendorPurchaseView.Offer offer,
        string label)
    {
        context.Check(MatchesPreparedTarget(offer), $"{label} declared offer identity",
            $"name='{offer.GeneratedName}' base='{offer.BaseName}'");
        context.Check(offer.Costs.Count == 1
                && offer.Costs[0].Count == 1
                && string.Equals(offer.Costs[0].Currency, ExpectedCurrency, StringComparison.Ordinal),
            $"{label} structural price", FormatCosts(offer.Costs));
        context.Check(IsUnavailable(offer.Costs), $"{label} unavailable color signature",
            FormatCosts(offer.Costs));
        context.Check(offer.IsVisible, $"{label} remains visible while unavailable",
            $"flags=0x{offer.ElementFlags:X8}");
    }

    private static bool MatchesPreparedTarget(VendorPurchaseView.Offer offer)
        => string.Equals(offer.BaseName, ExpectedBase, StringComparison.Ordinal)
        && offer.TooltipText.Contains(ExpectedName, StringComparison.OrdinalIgnoreCase);

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

    private static float DistanceSquared(ElementGeometry.Rect a, ElementGeometry.Rect b)
    {
        var dx = a.CenterX - b.CenterX;
        var dy = a.CenterY - b.CenterY;
        return dx * dx + dy * dy;
    }
}
