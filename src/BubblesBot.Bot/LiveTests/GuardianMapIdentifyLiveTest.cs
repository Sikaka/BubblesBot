using BubblesBot.Bot.Input;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Economic batch identification of carried Shaper Guardian maps. The complete Scroll of Wisdom
/// requirement is checked before the first click, and every application proves source, target,
/// currency decrement, and the item's Identified transition.
/// </summary>
public sealed class GuardianMapIdentifyLiveTest : ILiveTestCase
{
    private const string GuardianMapPath = "Metadata/Items/Maps/MapKeyShaperGuardian";
    private const string WisdomPath = "Metadata/Items/Currency/CurrencyIdentification";

    public string Id => "G-06-guardian-map-identify";
    public string Name => "Identify carried Shaper Guardian maps";
    public string Description => "Identifies every visible unidentified Shaper Guardian map with exact hover ownership and per-item currency/state postconditions.";
    public string ManualSetup => "Open inventory with all intended guardian maps and enough Scrolls of Wisdom visible, hold no item, and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Economic;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        var maps = GuardianMaps(before.Inventory);
        var targets = maps.Where(x => !x.Identified).Select(x => x.Rect!.Value).ToArray();
        var wisdom = CurrencyCount(before.Inventory, WisdomPath);
        context.Check(before.Inventory.IsOpen, "inventory open", $"items={before.Inventory.Items.Count}");
        context.Check(before.Cursor.Action == CursorView.CursorAction.Free,
            "cursor free", before.Cursor.Action.ToString());
        context.Check(maps.Length > 0, "carried guardian maps", $"count={maps.Length}");
        context.Observe("identification preflight",
            $"guardianMaps={maps.Length} unidentified={targets.Length} wisdom={wisdom}");
        if (!before.Inventory.IsOpen || before.Cursor.Action != CursorView.CursorAction.Free
            || maps.Length == 0)
            return LiveTestCaseResult.Blocked("guardian-map identification setup is incomplete", "PreparedStateMismatch");
        if (targets.Length == 0)
            return LiveTestCaseResult.Pass("all visible Shaper Guardian maps are already identified", "GuardianMapsAlreadyIdentified");
        if (wisdom < targets.Length)
            return LiveTestCaseResult.Blocked(
                $"need {targets.Length} visible Scrolls of Wisdom before starting; found {wisdom}",
                "InsufficientWisdomReserve");
        context.Check(wisdom >= targets.Length, "complete Wisdom reserve", $"{wisdom}/{targets.Length}");

        if (!await SelectWisdomAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail(
                "Scroll of Wisdom selection was not positively confirmed",
                "IdentificationCurrencySelectFailed");

        var identified = 0;
        foreach (var targetSlot in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Some client builds complete the Shift-application but expose CursorAction.Free
            // instead of retaining UseItem. Re-select only when the live cursor positively says
            // the currency is no longer armed; the slot itself remains untouched until then.
            if (context.Snapshot().Cursor.Action != CursorView.CursorAction.UseItem
                && !await SelectWisdomAsync(context, cancellationToken))
            {
                await CancelCurrencyAsync(context, cancellationToken);
                return LiveTestCaseResult.Fail(
                    "Scroll of Wisdom re-selection was not positively confirmed",
                    "IdentificationCurrencySelectFailed");
            }
            if (!await IdentifyOneAsync(context, targetSlot, cancellationToken))
            {
                await CancelCurrencyAsync(context, cancellationToken);
                return LiveTestCaseResult.Fail(
                    $"map {identified + 1}/{targets.Length} identification was not positively confirmed",
                    "IdentificationApplyFailed");
            }
            identified++;
        }
        await CancelCurrencyAsync(context, cancellationToken);

        var after = context.Snapshot();
        var remaining = GuardianMaps(after.Inventory).Count(x => !x.Identified);
        context.Check(remaining == 0, "all visible guardian maps identified", $"remaining={remaining}");
        return remaining == 0
            ? LiveTestCaseResult.Pass(
                $"identified {identified} Shaper Guardian maps; Scrolls consumed={identified}",
                "GuardianMapsIdentified")
            : LiveTestCaseResult.Fail(
                $"{remaining} guardian maps remain unidentified", "IdentificationIncomplete");
    }

    private static async Task<bool> SelectWisdomAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        var currency = before.Inventory.Items
            .Where(x => x.Path.Equals(WisdomPath, StringComparison.OrdinalIgnoreCase)
                && x.Rect is not null)
            .OrderByDescending(x => x.StackSize)
            .FirstOrDefault();
        if (currency.ItemEntity == 0 || currency.Rect is not { } currencyRect)
            return false;

        var currencyBefore = CurrencyCount(before.Inventory, WisdomPath);
        var sourcePoint = before.Window.ToScreen(currencyRect.CenterX, currencyRect.CenterY);
        await context.HoverAsync(sourcePoint.X, sourcePoint.Y, 180, cancellationToken);
        var sourceSnapshot = context.Snapshot();
        var sourceHover = UiHoverView.Read(sourceSnapshot.Reader, sourceSnapshot.IngameStateAddress);
        if (!HoverOwns(sourceSnapshot, sourceHover.Element, currency.ElementAddress)
            || !sourceHover.TooltipLines.Any(x =>
                x.Contains("Scroll of Wisdom", StringComparison.OrdinalIgnoreCase)))
        {
            context.Check(false, "Scroll of Wisdom source identity",
                string.Join(" | ", sourceHover.TooltipLines));
            return false;
        }
        context.Check(true, "Scroll of Wisdom source identity",
            $"element=0x{(long)currency.ElementAddress:X} stackTotal={currencyBefore}");

        var use = await context.VerifiedRightClickAsync(
            sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi, "select Scroll of Wisdom",
            () => context.Snapshot().Cursor.Action == CursorView.CursorAction.UseItem,
            2_000, cancellationToken);
        if (use != ActionOutcome.Confirmed) return false;
        return await context.WaitForInputIdleAsync(
            "after selecting Scroll of Wisdom", 1_500, cancellationToken);
    }

    private static async Task<bool> IdentifyOneAsync(
        LiveTestContext context,
        ElementGeometry.Rect targetSlot,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        var target = FindGuardianAtSlot(before.Inventory, targetSlot);
        if (target.ItemEntity == 0 || target.Identified || target.Rect is not { } targetRect
            || before.Cursor.Action != CursorView.CursorAction.UseItem)
            return false;
        var currencyBefore = CurrencyCount(before.Inventory, WisdomPath);

        var targetPoint = context.Snapshot().Window.ToScreen(targetRect.CenterX, targetRect.CenterY);
        await context.HoverAsync(targetPoint.X, targetPoint.Y, 180, cancellationToken);
        var hovered = context.Snapshot();
        var targetHover = UiHoverView.Read(hovered.Reader, hovered.IngameStateAddress);
        var liveTarget = FindGuardianAtSlot(hovered.Inventory, targetSlot);
        if (liveTarget.ItemEntity == 0
            || !HoverOwns(hovered, targetHover.Element, liveTarget.ElementAddress))
        {
            context.Check(false, "guardian map identification target identity",
                $"target=0x{(long)liveTarget.ElementAddress:X} hover=0x{(long)targetHover.Element:X}");
            await CancelCurrencyAsync(context, cancellationToken);
            return false;
        }
        context.Check(true, "guardian map identification target identity",
            $"slot=({targetSlot.CenterX:F0},{targetSlot.CenterY:F0}) entity=0x{(long)liveTarget.ItemEntity:X} base='{liveTarget.BaseName}'");

        var apply = await context.VerifiedModifierClickAsync(
            targetPoint.X, targetPoint.Y, [0x10], ClickIntent.InteractUi,
            "Shift-identify Shaper Guardian map",
            () =>
            {
                var current = context.Snapshot();
                var currentTarget = FindGuardianAtSlot(current.Inventory, targetSlot);
                return currentTarget.ItemEntity != 0
                    && currentTarget.Identified
                    && CurrencyCount(current.Inventory, WisdomPath) == currencyBefore - 1;
            }, 3_000, cancellationToken);
        return apply == ActionOutcome.Confirmed
            && await context.WaitForInputIdleAsync(
            "after identifying guardian map", 1_500, cancellationToken);
    }

    private static InventoryView.Item[] GuardianMaps(InventoryView inventory)
        => inventory.Items.Where(x =>
                x.Path.Equals(GuardianMapPath, StringComparison.OrdinalIgnoreCase)
                && x.Rect is not null)
            .OrderBy(x => x.Rect!.Value.Y)
            .ThenBy(x => x.Rect!.Value.X)
            .ToArray();

    private static int CurrencyCount(InventoryView inventory, string path)
        => inventory.Items.Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Sum(x => Math.Max(1, x.StackSize));

    private static InventoryView.Item FindGuardianAtSlot(
        InventoryView inventory,
        ElementGeometry.Rect slot)
        => inventory.Items.FirstOrDefault(x =>
            x.Path.Equals(GuardianMapPath, StringComparison.OrdinalIgnoreCase)
            && x.Rect is { } rect
            && Math.Abs(rect.CenterX - slot.CenterX) < 2f
            && Math.Abs(rect.CenterY - slot.CenterY) < 2f);

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

    private static async Task CancelCurrencyAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        if (context.Snapshot().Cursor.Action != CursorView.CursorAction.UseItem) return;
        await context.VerifiedTapKeyAsync(
            0x1B, ClickIntent.InteractUi, "cancel selected identification currency",
            () => context.Snapshot().Cursor.Action == CursorView.CursorAction.Free,
            2_000, cancellationToken);
    }
}
