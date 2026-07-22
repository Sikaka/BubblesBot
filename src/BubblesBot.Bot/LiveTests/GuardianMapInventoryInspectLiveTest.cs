using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read/hover sweep of every carried Shaper Guardian map; never consumes currency.</summary>
public sealed class GuardianMapInventoryInspectLiveTest : ILiveTestCase
{
    private const string GuardianMapPath = "Metadata/Items/Maps/MapKeyShaperGuardian";

    public string Id => "G-05-guardian-map-inventory-inspect";
    public string Name => "Inspect carried Shaper Guardian maps";
    public string Description => "Hovers every carried Shaper Guardian map and records exact identity, rarity, item quantity, flattened stats, and forbidden modifiers.";
    public string ManualSetup => "Open inventory with the Shaper Guardian maps visible, hold no item, and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var initial = context.Snapshot();
        context.Check(initial.Inventory.IsOpen, "inventory open", $"items={initial.Inventory.Items.Count}");
        var maps = initial.Inventory.Items
            .Where(x => x.Path.Equals(GuardianMapPath, StringComparison.OrdinalIgnoreCase)
                && x.Rect is not null)
            .OrderBy(x => x.Rect!.Value.Y)
            .ThenBy(x => x.Rect!.Value.X)
            .ToArray();
        context.Check(maps.Length > 0, "carried guardian maps", $"count={maps.Length}");
        if (!initial.Inventory.IsOpen || maps.Length == 0)
            return LiveTestCaseResult.Blocked("no visible carried Shaper Guardian maps", "PreparedStateMismatch");

        var identified = new List<string>();
        var identifiedCount = 0;
        var unsafeCount = 0;
        var unidentifiedCount = 0;
        for (var i = 0; i < maps.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = maps[i];
            if (baseline.Rect is not { } rect) continue;
            var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
            await context.HoverAsync(point.X, point.Y, 180, cancellationToken);

            var snapshot = context.Snapshot();
            var live = snapshot.Inventory.Items.FirstOrDefault(x => x.ItemEntity == baseline.ItemEntity);
            var hover = UiHoverView.Read(snapshot.Reader, snapshot.IngameStateAddress);
            var exact = live.ItemEntity != 0 && HoverOwns(snapshot, hover.Element, live.ElementAddress);
            context.Check(exact, $"guardian map {i + 1}/{maps.Length} exact hover",
                $"target=0x{(long)live.ElementAddress:X} hover=0x{(long)hover.Element:X}");
            if (!exact || hover.TooltipLines.Count == 0)
                return LiveTestCaseResult.Fail($"guardian map {i + 1} tooltip identity failed", "TooltipMismatch");

            var mapName = ResolveMapName(live.BaseName, hover.TooltipLines);
            var tooltip = GuardianInvitationPolicy.EvaluateTooltip(hover.TooltipLines);
            var semantic = MapModifierCatalog.EvaluateTooltip(hover.TooltipLines);
            var quantity = tooltip.ItemQuantity >= 0
                ? tooltip.ItemQuantity
                : live.Rarity == EntityListReader.EntityRarity.Normal ? 0 : -1;
            var stats = live.Stats ?? [];
            var statVerdict = MapStatCatalog.Evaluate(stats);
            if (!live.Identified) unidentifiedCount++;
            else
            {
                identifiedCount++;
                if (tooltip.HasForbiddenModifier) unsafeCount++;
            }
            if (mapName.Length > 0) identified.Add(mapName);
            context.Observe("guardian map",
                $"index={i + 1}/{maps.Length} map='{mapName}' base='{live.BaseName}' rarity={live.Rarity} identified={live.Identified} " +
                $"quantity={quantity}% modifierState={(live.Identified ? "known" : "unknown-unidentified")} " +
                $"forbidden=[{string.Join(" | ", tooltip.ForbiddenLines)}] " +
                $"semantic=[{string.Join(',', semantic.Matches.Select(x => $"{x.Definition.Key}:{x.Disposition}"))}] " +
                $"catalogVetoIds=[{string.Join(',', statVerdict.VetoHits)}] " +
                $"stats=[{string.Join(',', stats.Select(x => $"{x.Id}:{x.Value}"))}] " +
                $"tooltip=[{string.Join(" || ", hover.TooltipLines)}]");
            context.Check(true, $"guardian map {i + 1}/{maps.Length} identity model",
                !live.Identified
                    ? "modifier state deferred until identified"
                    : mapName.Length > 0
                        ? mapName
                        : "generic Shaper Guardian key; Atlas node supplies guardian identity");
            context.Check(quantity >= 0, $"guardian map {i + 1}/{maps.Length} item quantity", quantity.ToString());
            context.Check(!live.Identified || semantic.Matches.Count > 0,
                $"guardian map {i + 1}/{maps.Length} semantic catalog coverage",
                live.Identified
                    ? string.Join(" | ", semantic.Matches.Select(x => x.Definition.Key))
                    : "deferred until identified");
        }

        var distinct = identified.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        context.Check(identifiedCount + unidentifiedCount == maps.Length, "all guardian map states classified",
            $"identified={identifiedCount} unidentified={unidentifiedCount} total={maps.Length}");
        if (unidentifiedCount > 0)
            context.Check(true, "guardian identification required", $"unidentified={unidentifiedCount}");
        else
            context.Check(true, "generic guardian consumables ready",
                "guardian family is selected by the Atlas node, not encoded in the item tooltip");
        return LiveTestCaseResult.Pass(
            $"inspected {maps.Length} Shaper Guardian maps; identified={identifiedCount}, unidentified={unidentifiedCount}, forbidden known rolls={unsafeCount}",
            unidentifiedCount == 0 ? "GuardianMapsInspected" : "GuardianMapsNeedIdentification");
    }

    private static string ResolveMapName(string baseName, IEnumerable<string> tooltipLines)
        => GuardianRotationPolicy.Maps.FirstOrDefault(map =>
            baseName.Contains(map, StringComparison.OrdinalIgnoreCase)
            || tooltipLines.Any(line => line.Contains(map, StringComparison.OrdinalIgnoreCase))) ?? string.Empty;

    private static bool HoverOwns(GameSnapshot snapshot, nint hover, nint target)
    {
        var current = hover;
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return false;
    }
}
