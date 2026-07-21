using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Reversible vendor-navigation test: move from the operator-confirmed offer to a different offer,
/// prove UIHover and rendered-tooltip identity, then restore the original hover. Never clicks.
/// </summary>
public sealed class VendorHoverRoundTripLiveTest : ILiveTestCase
{
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow" };

    public string Id => "U-02-vendor-hover-roundtrip";
    public string Name => "Vendor offer hover round trip";
    public string Description => "Uses production cursor movement to identify another vendor offer and restore the original hover without clicking.";
    public string ManualSetup => "Open a normal NPC purchase shop, hover any priced offer, and leave PoE focused. Do not hold an item on the cursor.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var baseline = VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        context.Check(baseline.IsOpen, "purchase window baseline", $"panel=0x{(long)baseline.Panel:X}");
        context.Check(baseline.Offers.Count >= 2, "vendor offers enumerated", $"count={baseline.Offers.Count}");

        var original = baseline.HoveredOffer;
        if (original is null || original.Rect is null)
            return LiveTestCaseResult.Blocked("the prepared hover did not resolve to one vendor offer", "HoverSetupMissing");

        var window = snapshot.Window;
        if (!original.Rect.Value.IntersectsWindow(window.Width, window.Height))
            return LiveTestCaseResult.Blocked("the prepared offer rectangle is outside the PoE window", "InvalidGeometry");

        CheckOffer(context, original, "original");
        var baselineFingerprint = Fingerprint(baseline.Offers);
        var target = baseline.Offers
            .Where(x => x.Element != original.Element
                && x.Rect is { } rect
                && rect.IntersectsWindow(window.Width, window.Height))
            .OrderByDescending(x => DistanceSquared(x.Rect!.Value, original.Rect.Value))
            .FirstOrDefault();
        if (target is null)
            return LiveTestCaseResult.Blocked("no distinct on-screen vendor offer is available", "NoAlternateOffer");

        var targetPoint = AbsoluteCenter(window, target.Rect!.Value);
        context.Observe("alternate hover target",
            $"element=0x{(long)target.Element:X} base='{target.BaseName}' point={targetPoint.X},{targetPoint.Y}");
        await context.HoverAsync(targetPoint.X, targetPoint.Y, 175, cancellationToken);
        var reachedAlternate = await context.WaitUntilAsync(
            "alternate vendor hover",
            () => Read(context).HoveredOffer?.Element == target.Element,
            timeoutMs: 2_000,
            cancellationToken);
        if (!reachedAlternate)
            return LiveTestCaseResult.Fail("cursor moved but UIHover did not resolve to the intended alternate offer", "TooltipMismatch");

        var alternate = Read(context).HoveredOffer;
        if (alternate is null)
            return LiveTestCaseResult.Fail("alternate UIHover resolved without an offer", "TooltipMismatch");
        CheckOffer(context, alternate, "alternate");
        context.Check(alternate.Element == target.Element, "alternate element identity",
            $"expected=0x{(long)target.Element:X} observed=0x{(long)alternate.Element:X}");
        context.Check(alternate.Item == target.Item, "alternate item identity",
            $"expected=0x{(long)target.Item:X} observed=0x{(long)alternate.Item:X}");

        var originalPoint = AbsoluteCenter(window, original.Rect.Value);
        await context.HoverAsync(originalPoint.X, originalPoint.Y, 175, cancellationToken);
        var restored = await context.WaitUntilAsync(
            "original vendor hover restore",
            () => Read(context).HoveredOffer?.Element == original.Element,
            timeoutMs: 2_000,
            cancellationToken);
        if (!restored)
            return LiveTestCaseResult.Fail("alternate hover passed but original hover was not restored", "RestoreFailed");

        var final = Read(context);
        var finalOriginal = final.HoveredOffer;
        if (finalOriginal is not null) CheckOffer(context, finalOriginal, "restored original");
        context.Check(final.IsOpen, "purchase window remained open", $"panel=0x{(long)final.Panel:X}");
        context.Check(Fingerprint(final.Offers) == baselineFingerprint, "vendor inventory unchanged",
            $"offers={final.Offers.Count}");

        return LiveTestCaseResult.Pass(
            "alternate offer hover was identified and original hover exactly restored",
            "CompletedAndRestored");
    }

    private static VendorPurchaseView Read(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static void CheckOffer(
        LiveTestContext context,
        VendorPurchaseView.Offer offer,
        string label)
    {
        var tooltip = offer.TooltipText;
        context.Check(!string.IsNullOrWhiteSpace(offer.BaseName), $"{label} base identity", offer.BaseName);
        context.Check(offer.RenderedTooltip != 0 && offer.TooltipLines.Count > 0,
            $"{label} rendered tooltip", $"root=0x{(long)offer.RenderedTooltip:X} lines={offer.TooltipLines.Count}");
        context.Check(tooltip.Contains(offer.BaseName, StringComparison.OrdinalIgnoreCase),
            $"{label} tooltip/base agreement", offer.GeneratedName);
        context.Check(offer.CostCount is > 0 && !string.IsNullOrWhiteSpace(offer.CostCurrency),
            $"{label} price", $"{offer.CostCount}x {offer.CostCurrency}");
        context.Observe($"{label} requirements",
            $"str={offer.RequiredAttributes.Strength} dex={offer.RequiredAttributes.Dexterity} int={offer.RequiredAttributes.Intelligence}");
    }

    private static (int X, int Y) AbsoluteCenter(WindowInfo window, ElementGeometry.Rect rect)
        => (window.OriginX + (int)MathF.Round(rect.CenterX),
            window.OriginY + (int)MathF.Round(rect.CenterY));

    private static float DistanceSquared(ElementGeometry.Rect a, ElementGeometry.Rect b)
    {
        var dx = a.CenterX - b.CenterX;
        var dy = a.CenterY - b.CenterY;
        return dx * dx + dy * dy;
    }

    private static string Fingerprint(IReadOnlyList<VendorPurchaseView.Offer> offers)
        => string.Join('|', offers
            .OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{(long)x.Item:X}:{x.Metadata}:{x.BaseName}"));
}
