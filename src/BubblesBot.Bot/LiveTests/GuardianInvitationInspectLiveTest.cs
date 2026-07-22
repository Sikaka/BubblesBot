using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Hover and fingerprint the staged The Formed invitation without spending currency.</summary>
public sealed class GuardianInvitationInspectLiveTest : ILiveTestCase
{
    public string Id => "G-03-formed-item-inspect";
    public string Name => "Inspect staged The Formed invitation";
    public string Description => "Proves the legacy Map Receptacle item identity, hovers it, and records rarity, stats, tooltip modifiers, item quantity, and unsafe-mod verdict.";
    public string ManualSetup => "Open Invitation: The Formed through Kirac so the Map Receptacle and inventory are visible. Hold no item and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var initial = context.Snapshot();
        var receptacle = initial.MapReceptacle;
        context.Check(receptacle.IsVisible, "Map Receptacle", $"panel=0x{(long)receptacle.Panel:X}");
        var item = receptacle.Item();
        context.Check(item is not null, "staged invitation item", item is null
            ? "missing"
            : $"element=0x{(long)item.Value.Element:X} entity=0x{(long)item.Value.ItemEntity:X} path='{item.Value.Path}' base='{item.Value.BaseName}' rarity={item.Value.Rarity}");
        if (!receptacle.IsVisible || item is not { Rect: { } rect } staged)
            return LiveTestCaseResult.Blocked("The Formed is not staged in the Map Receptacle", "PreparedStateMismatch");

        var window = initial.Window;
        if (!rect.IntersectsWindow(window.Width, window.Height))
            return LiveTestCaseResult.Blocked("staged invitation is off-screen", "InvalidGeometry");
        var point = window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 350, cancellationToken);

        var hovered = context.Snapshot();
        var hover = UiHoverView.Read(hovered.Reader, hovered.IngameStateAddress);
        var exact = HoverOwns(hovered.Reader, hover.Element, staged.Element);
        context.Check(exact, "staged invitation exact hover",
            $"target=0x{(long)staged.Element:X} hover=0x{(long)hover.Element:X}");
        context.Check(hover.TooltipRoot != 0 && hover.TooltipLines.Count > 0,
            "staged invitation tooltip", $"root=0x{(long)hover.TooltipRoot:X} lines={hover.TooltipLines.Count}");
        context.Observe("staged invitation tooltip", string.Join(" || ", hover.TooltipLines));

        var liveItem = hovered.MapReceptacle.Item();
        if (!exact || liveItem is null || hover.TooltipLines.Count == 0)
            return LiveTestCaseResult.Fail("staged invitation identity or tooltip was unreadable", "TooltipMismatch");
        var verdict = MapStatCatalog.Evaluate(liveItem.Value.Stats);
        var tooltipState = GuardianInvitationPolicy.EvaluateTooltip(hover.TooltipLines);
        context.Observe("staged invitation stats",
            $"quantity={verdict.Quantity} rarity={verdict.Rarity} packSize={verdict.PackSize} " +
            $"vetoIds=[{string.Join(',', verdict.VetoHits)}] stats=[{string.Join(',', liveItem.Value.Stats.Select(x => $"{x.Id}:{x.Value}"))}]");
        context.Check(tooltipState.IsTheFormed, "The Formed tooltip identity", tooltipState.Name);
        var quantity = tooltipState.ItemQuantity >= 0
            ? tooltipState.ItemQuantity
            : liveItem.Value.Rarity == EntityListReader.EntityRarity.Normal ? 0 : -1;
        context.Check(quantity >= 0, "invitation item quantity", quantity.ToString());
        context.Check(!tooltipState.HasForbiddenModifier, "invitation forbidden modifiers",
            tooltipState.HasForbiddenModifier ? string.Join(" | ", tooltipState.ForbiddenLines) : "none");

        return LiveTestCaseResult.Pass(
            $"The Formed inspected: rarity={liveItem.Value.Rarity}, quantity={quantity}%, forbidden={tooltipState.ForbiddenLines.Count}",
            "InvitationInspected");
    }

    private static bool HoverOwns(BubblesBot.Core.MemoryReader reader, nint hover, nint target)
    {
        var current = hover;
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!reader.TryReadStruct<nint>(current + BubblesBot.Core.Game.KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return false;
    }
}
