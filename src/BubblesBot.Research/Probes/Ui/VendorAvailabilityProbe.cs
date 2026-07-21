using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Ui;

/// <summary>Read-only capture of raw affordance signals for one hovered vendor offer.</summary>
public sealed class VendorAvailabilityProbe : IProbe
{
    public string Name => "ui.vendor-availability";
    public string Group => "ui";
    public string Description => "Capture hovered vendor offer flags and structural cost-row colors without input.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx) => Capture(ctx);
    public ProbeResult Discover(ProbeContext ctx) => Capture(ctx);

    private static ProbeResult Capture(ProbeContext ctx)
    {
        var view = VendorPurchaseView.Read(ctx.Reader, ctx.Chain.IngameState);
        if (!view.IsOpen) return ProbeResult.Skip("PurchaseWindow is not open");
        var offer = view.HoveredOffer;
        if (offer is null)
            return ProbeResult.Fail($"UIHover 0x{(long)view.HoverElement:X} did not resolve one vendor offer");
        var expectedName = GetOption(ctx.Arguments, "--expect-name");
        var expectedBase = GetOption(ctx.Arguments, "--expect-base");
        var expectedCostCount = GetIntOption(ctx.Arguments, "--expect-cost-count");
        var expectedCostCurrency = GetOption(ctx.Arguments, "--expect-cost-currency");
        var expectedAvailability = GetOption(ctx.Arguments, "--expect-availability")?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedName)
            && !offer.TooltipText.Contains(expectedName, StringComparison.OrdinalIgnoreCase))
            return ProbeResult.Fail($"expected name '{expectedName}' was absent from the rendered tooltip");
        if (!string.IsNullOrWhiteSpace(expectedBase)
            && !string.Equals(offer.BaseName, expectedBase, StringComparison.Ordinal))
            return ProbeResult.Fail($"expected base '{expectedBase}' but read '{offer.BaseName}'");
        if (expectedCostCount is { } count && !offer.Costs.Any(x => x.Count == count))
            return ProbeResult.Fail($"expected structural cost count {count} but read [{FormatCosts(offer.Costs)}]");
        if (!string.IsNullOrWhiteSpace(expectedCostCurrency)
            && !offer.Costs.Any(x => string.Equals(x.Currency, expectedCostCurrency, StringComparison.OrdinalIgnoreCase)))
            return ProbeResult.Fail($"expected structural cost currency '{expectedCostCurrency}' but read [{FormatCosts(offer.Costs)}]");
        if (offer.Costs.Count == 0)
            return ProbeResult.Fail("hovered offer exposed no structural cost rows");
        var unavailable = IsUnavailable(offer.Costs);
        if (expectedAvailability is not null and not ("available" or "unavailable"))
            return ProbeResult.Fail("--expect-availability must be 'available' or 'unavailable'");
        if (expectedAvailability == "unavailable" && !unavailable)
            return ProbeResult.Fail($"expected unavailable red cost rows but read [{FormatCosts(offer.Costs)}]");
        if (expectedAvailability == "available" && unavailable)
            return ProbeResult.Fail($"expected available cost rows but read unavailable red rows [{FormatCosts(offer.Costs)}]");

        return ProbeResult.Pass(
            $"hover=0x{(long)view.HoverElement:X} offer=0x{(long)offer.Element:X} "
            + $"base='{offer.BaseName}' name='{offer.GeneratedName}' visible={offer.IsVisible} "
            + $"flags=0x{offer.ElementFlags:X8} availability={(unavailable ? "unavailable" : "available/unknown")} "
            + $"costs=[{FormatCosts(offer.Costs)}]");
    }

    private static string FormatCosts(IReadOnlyList<VendorPurchaseView.CostEntry> costs)
        => string.Join(" + ", costs.Select(x =>
            $"{x.Count}x {x.Currency} countColor={FormatColor(x.CountTextColor)} "
            + $"currencyColor={FormatColor(x.CurrencyTextColor)}"));

    private static string FormatColor(ColorBGRA color)
        => $"BGRA({color.B},{color.G},{color.R},{color.A})";

    private static bool IsUnavailable(IReadOnlyList<VendorPurchaseView.CostEntry> costs)
        => costs.Count > 0 && costs.All(x => IsUnavailableRed(x.CountTextColor)
            && IsUnavailableRed(x.CurrencyTextColor));

    private static bool IsUnavailableRed(ColorBGRA color)
        => color.A > 0 && color.R >= 180 && color.G <= 80 && color.B <= 80;

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        var index = args.ToList().FindIndex(x => string.Equals(x, name, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static int? GetIntOption(IReadOnlyList<string> args, string name)
        => int.TryParse(GetOption(args, name), out var value) ? value : null;
}
