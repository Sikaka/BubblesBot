using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Hover every visible vendor offer and prove tooltip identity and price without clicks.</summary>
public sealed class VendorVisibleOfferSweepLiveTest : ILiveTestCase
{
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow" };

    public string Id => "U-03-vendor-visible-offer-sweep";
    public string Name => "Visible vendor offer identity/price sweep";
    public string Description => "Moves across every visible offer, proving UIHover, item/base, rendered tooltip, price, and stable shop contents without clicking.";
    public string ManualSetup => "Open the vendor page to validate, leave no item held or offer highlighted, and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var baseline = VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        var visible = baseline.Offers
            .Where(x => x.IsVisible
                && x.Rect is { } rect
                && rect.IntersectsWindow(snapshot.Window.Width, snapshot.Window.Height))
            .OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .ToArray();
        context.Check(baseline.IsOpen, "purchase window baseline", $"panel=0x{(long)baseline.Panel:X}");
        context.Check(visible.Length > 0, "visible vendor offers", $"visible={visible.Length} allocated={baseline.Offers.Count}");
        if (!baseline.IsOpen || visible.Length == 0)
            return LiveTestCaseResult.Blocked("the prepared vendor page has no visible readable offers", "OfferSetupMissing");

        var baselineFingerprint = Fingerprint(baseline.Offers);
        var original = baseline.HoveredOffer;
        var validated = 0;
        foreach (var target in visible)
        {
            var point = AbsoluteCenter(snapshot.Window, target.Rect!.Value);
            await context.HoverAsync(point.X, point.Y, 90, cancellationToken);
            var reached = await context.WaitUntilAsync(
                $"offer hover {validated + 1}/{visible.Length}",
                () => Read(context).HoveredOffer?.Element == target.Element,
                timeoutMs: 1_500,
                cancellationToken,
                pollMs: 20);
            if (!reached)
                return LiveTestCaseResult.Fail(
                    $"UIHover did not resolve offer {validated + 1}/{visible.Length} '{target.BaseName}'",
                    "TooltipMismatch");

            var current = Read(context).HoveredOffer;
            if (current is null)
                return LiveTestCaseResult.Fail("UIHover resolved without a vendor offer", "TooltipMismatch");
            var label = $"offer {validated + 1}/{visible.Length}";
            context.Check(current.Item == target.Item && current.Element == target.Element,
                $"{label} exact identity", $"base='{current.BaseName}' item=0x{(long)current.Item:X}");
            context.Check(!string.IsNullOrWhiteSpace(current.BaseName)
                    && current.TooltipText.Contains(current.BaseName, StringComparison.OrdinalIgnoreCase),
                $"{label} tooltip/base agreement", current.GeneratedName);
            context.Check(current.RenderedTooltip != 0 && current.TooltipLines.Count > 0,
                $"{label} rendered tooltip", $"root=0x{(long)current.RenderedTooltip:X} lines={current.TooltipLines.Count}");
            context.Check(current.Costs.Count > 0
                    && current.Costs.All(x => x.Count > 0 && !string.IsNullOrWhiteSpace(x.Currency)),
                $"{label} structural price",
                string.Join(" + ", current.Costs.Select(x => $"{x.Count}x {x.Currency}")));
            context.Check(current.Rect == target.Rect && current.IsVisible,
                $"{label} stable visible geometry", FormatRect(current.Rect));
            validated++;
        }

        await RestoreHoverAsync(context, snapshot.Window, original, baseline.PageControls, cancellationToken);
        var final = Read(context);
        context.Check(Fingerprint(final.Offers) == baselineFingerprint,
            "vendor contents unchanged", $"validated={validated} allocated={final.Offers.Count}");
        context.Check(validated == visible.Length, "all visible offers validated",
            $"validated={validated}/{visible.Length}");
        return LiveTestCaseResult.Pass(
            $"validated exact identity, rendered tooltip, price, and geometry for all {validated} visible offers",
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
        if (page?.Rect is not { } pageRect) return;
        var safePoint = AbsoluteCenter(window, pageRect);
        await context.HoverAsync(safePoint.X, safePoint.Y, 120, cancellationToken);
        var snapshot = context.Snapshot();
        snapshot.Reader.TryReadStruct<nint>(
            snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
        context.Check(hover == 0, "unhighlighted hover state restored", $"UIHover=0x{(long)hover:X}");
    }

    private static VendorPurchaseView Read(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static string Fingerprint(IReadOnlyList<VendorPurchaseView.Offer> offers)
        => string.Join('|', offers.OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{x.Metadata}:{x.BaseName}:{x.IsVisible}:"
                + $"{x.RequiredAttributes.Strength},{x.RequiredAttributes.Dexterity},{x.RequiredAttributes.Intelligence}"));

    private static (int X, int Y) AbsoluteCenter(WindowInfo window, ElementGeometry.Rect rect)
        => (window.OriginX + (int)MathF.Round(rect.CenterX),
            window.OriginY + (int)MathF.Round(rect.CenterY));

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";
}
