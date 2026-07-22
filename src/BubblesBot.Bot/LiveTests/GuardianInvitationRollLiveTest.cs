using BubblesBot.Bot.Input;
using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Economic, bounded invitation roll: Alchemy for Normal, then Chaos only while a forbidden mod
/// remains. Every application requires exact source/target hover and currency + item deltas.
/// </summary>
public sealed class GuardianInvitationRollLiveTest : ILiveTestCase
{
    private const string AlchemyPath = "Metadata/Items/Currency/CurrencyUpgradeToRare";
    private const string ChaosPath = "Metadata/Items/Currency/CurrencyRerollRare";
    private const int MaxChaosRerolls = 10;
    private const int MinimumItemQuantity = 65;
    private const int PreferredItemQuantity = 75;

    public string Id => "G-04-formed-roll";
    public string Name => "Roll The Formed invitation safely";
    public string Description => "Uses Alchemy/Chaos until The Formed has no forbidden modifiers and at least 65% item quantity, preferring 75%+, with every roll recorded.";
    public string ManualSetup => "The Formed is staged in the visible Map Receptacle; inventory contains Orb of Alchemy and Chaos Orbs; cursor is free. Keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Economic;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        var staged = before.MapReceptacle.Item();
        context.Check(before.MapReceptacle.IsVisible && staged is not null,
            "staged The Formed", staged is null ? "missing" : FormatItem(staged.Value));
        context.Check(before.Inventory.IsOpen, "inventory open", $"items={before.Inventory.Items.Count}");
        context.Check(before.Cursor.Action == CursorView.CursorAction.Free,
            "cursor free", before.Cursor.Action.ToString());
        if (!before.MapReceptacle.IsVisible || staged is null || !before.Inventory.IsOpen
            || before.Cursor.Action != CursorView.CursorAction.Free)
            return LiveTestCaseResult.Blocked("invitation/currency rolling setup is incomplete", "PreparedStateMismatch");
        if (!staged.Value.BaseName.Contains("The Formed", StringComparison.OrdinalIgnoreCase))
            return LiveTestCaseResult.Blocked("staged item is not The Formed", "WrongInvitation");

        var alchemy = CurrencyCount(before.Inventory, AlchemyPath);
        var chaos = CurrencyCount(before.Inventory, ChaosPath);
        context.Observe("rolling currency", $"alchemy={alchemy} chaos={chaos}");

        var state = await InspectAsync(context, 0, cancellationToken);
        if (!state.Valid)
            return LiveTestCaseResult.Fail("staged invitation tooltip was unreadable", "TooltipUnreadable");
        if (staged.Value.Rarity is not EntityListReader.EntityRarity.Normal
            and not EntityListReader.EntityRarity.Rare)
            return LiveTestCaseResult.Blocked(
                $"unsupported invitation rarity {staged.Value.Rarity}", "UnsupportedRarity");

        var needsRoll = staged.Value.Rarity == EntityListReader.EntityRarity.Normal
            || state.Tooltip.HasForbiddenModifier
            || state.Quantity < MinimumItemQuantity;
        if (staged.Value.Rarity == EntityListReader.EntityRarity.Normal && alchemy < 1)
            return LiveTestCaseResult.Blocked("no Orb of Alchemy is visible in inventory", "MissingCurrency");
        // A fresh Alchemy result is unknowable, and an unsafe Rare may take several attempts.
        // Reserve the whole bounded budget before the first economic click so a run never strands
        // the invitation halfway through its own declared reroll transaction.
        if (needsRoll && chaos < MaxChaosRerolls)
            return LiveTestCaseResult.Blocked(
                $"bounded roll requires {MaxChaosRerolls} visible Chaos Orbs; found {chaos}",
                "InsufficientChaosReserve");
        context.Check(!needsRoll || chaos >= MaxChaosRerolls, "invitation Chaos reserve",
            needsRoll ? $"{chaos}/{MaxChaosRerolls}" : "no reroll required");

        var rolls = 0;
        var chaosUsed = 0;
        if (staged.Value.Rarity == EntityListReader.EntityRarity.Normal)
        {
            if (!await ApplyCurrencyAsync(context, AlchemyPath, "Orb of Alchemy", cancellationToken))
                return LiveTestCaseResult.Fail("Alchemy application was not positively confirmed", "AlchemyApplyFailed");
            rolls++;
            state = await InspectAsync(context, rolls, cancellationToken);
            if (!state.Valid)
                return LiveTestCaseResult.Fail("rolled invitation tooltip was unreadable", "TooltipUnreadable");
            await CancelCurrencyAsync(context, cancellationToken);
        }

        while ((state.Tooltip.HasForbiddenModifier || state.Quantity < MinimumItemQuantity)
            && chaosUsed < MaxChaosRerolls)
        {
            if (CurrencyCount(context.Snapshot().Inventory, ChaosPath) < 1)
                return LiveTestCaseResult.Fail("forbidden roll remains but no Chaos Orb is visible", "MissingChaos");
            if (!await ApplyCurrencyAsync(context, ChaosPath, "Chaos Orb", cancellationToken))
                return LiveTestCaseResult.Fail("Chaos application was not positively confirmed", "ChaosApplyFailed");
            rolls++;
            chaosUsed++;
            state = await InspectAsync(context, rolls, cancellationToken);
            if (!state.Valid)
                return LiveTestCaseResult.Fail("rerolled invitation tooltip was unreadable", "TooltipUnreadable");
        }

        context.Check(!state.Tooltip.HasForbiddenModifier, "final invitation modifiers",
            state.Tooltip.HasForbiddenModifier
                ? string.Join(" | ", state.Tooltip.ForbiddenLines)
                : "none forbidden");
        if (state.Tooltip.HasForbiddenModifier)
        {
            await CancelCurrencyAsync(context, cancellationToken);
            return LiveTestCaseResult.Fail(
                $"forbidden modifiers remain after {MaxChaosRerolls} Chaos rerolls", "RerollBudgetExhausted");
        }
        await CancelCurrencyAsync(context, cancellationToken);
        context.Check(state.Quantity >= MinimumItemQuantity, "final invitation quantity",
            $"{state.Quantity}% (minimum {MinimumItemQuantity}%)");
        if (state.Quantity < MinimumItemQuantity)
            return LiveTestCaseResult.Fail(
                $"invitation remains below {MinimumItemQuantity}% after {MaxChaosRerolls} Chaos rerolls",
                "QuantityBudgetExhausted");
        context.Observe("invitation quantity target",
            state.Quantity >= PreferredItemQuantity
                ? $"preferred target reached: {state.Quantity}% >= {PreferredItemQuantity}%"
                : $"minimum accepted: {state.Quantity}% >= {MinimumItemQuantity}%; preferred is {PreferredItemQuantity}%+");
        return LiveTestCaseResult.Pass(
            $"The Formed is Rare and runnable at {state.Quantity}% item quantity; rolls={rolls}, chaos={chaosUsed}",
            "InvitationRolledSafe");
    }

    private static async Task<bool> ApplyCurrencyAsync(
        LiveTestContext context,
        string path,
        string expectedName,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        if (before.Cursor.Action != CursorView.CursorAction.UseItem)
        {
            var currency = before.Inventory.Items
                .Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && x.Rect is not null)
                .OrderByDescending(x => x.StackSize)
                .FirstOrDefault();
            if (currency.ItemEntity == 0 || currency.Rect is not { } currencyRect)
                return false;
            var sourcePoint = before.Window.ToScreen(currencyRect.CenterX, currencyRect.CenterY);
            await context.HoverAsync(sourcePoint.X, sourcePoint.Y, 180, cancellationToken);
            var sourceSnapshot = context.Snapshot();
            var sourceHover = UiHoverView.Read(sourceSnapshot.Reader, sourceSnapshot.IngameStateAddress);
            if (!HoverOwns(sourceSnapshot, sourceHover.Element, currency.ElementAddress)
                || !sourceHover.TooltipLines.Any(x => x.Contains(expectedName, StringComparison.OrdinalIgnoreCase)))
            {
                context.Check(false, $"{expectedName} source identity", string.Join(" | ", sourceHover.TooltipLines));
                return false;
            }
            context.Check(true, $"{expectedName} source identity",
                $"element=0x{(long)currency.ElementAddress:X} stackTotal={CurrencyCount(before.Inventory, path)}");

            var use = await context.VerifiedRightClickAsync(
                sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi, $"select {expectedName}",
                () => context.Snapshot().Cursor.Action == CursorView.CursorAction.UseItem,
                2_000, cancellationToken);
            if (use != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync($"after selecting {expectedName}", 1_500, cancellationToken))
                return false;
        }

        before = context.Snapshot();
        var target = before.MapReceptacle.Item();
        if (target is not { Rect: { } targetRect }) return false;
        var currencyBefore = CurrencyCount(before.Inventory, path);
        var itemBefore = ItemFingerprint(target.Value);

        var targetPoint = before.Window.ToScreen(targetRect.CenterX, targetRect.CenterY);
        await context.HoverAsync(targetPoint.X, targetPoint.Y, 180, cancellationToken);
        var targetSnapshot = context.Snapshot();
        var targetHover = UiHoverView.Read(targetSnapshot.Reader, targetSnapshot.IngameStateAddress);
        if (!HoverOwns(targetSnapshot, targetHover.Element, target.Value.Element))
        {
            context.Check(false, $"{expectedName} target identity",
                $"target=0x{(long)target.Value.Element:X} hover=0x{(long)targetHover.Element:X}");
            await CancelCurrencyAsync(context, cancellationToken);
            return false;
        }
        context.Check(true, $"{expectedName} target identity", target.Value.BaseName);

        var apply = await context.VerifiedModifierClickAsync(
            targetPoint.X, targetPoint.Y, [0x10], ClickIntent.InteractUi,
            $"Shift-apply {expectedName} to The Formed",
            () =>
            {
                var current = context.Snapshot();
                var currentItem = current.MapReceptacle.Item();
                return CurrencyCount(current.Inventory, path) == currencyBefore - 1
                    && currentItem is not null
                    && ItemFingerprint(currentItem.Value) != itemBefore;
            }, 3_000, cancellationToken);
        if (apply != ActionOutcome.Confirmed)
        {
            await CancelCurrencyAsync(context, cancellationToken);
            return false;
        }
        return await context.WaitForInputIdleAsync($"after applying {expectedName}", 1_500, cancellationToken);
    }

    private static async Task<(bool Valid, GuardianInvitationState Tooltip, int Quantity)> InspectAsync(
        LiveTestContext context,
        int roll,
        CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var item = snapshot.MapReceptacle.Item();
        if (item is not { Rect: { } rect }) return (false, default, -1);
        var point = snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 250, cancellationToken);
        var hovered = context.Snapshot();
        var hover = UiHoverView.Read(hovered.Reader, hovered.IngameStateAddress);
        var liveItem = hovered.MapReceptacle.Item();
        if (liveItem is null || !HoverOwns(hovered, hover.Element, liveItem.Value.Element))
            return (false, default, -1);
        var tooltip = GuardianInvitationPolicy.EvaluateTooltip(hover.TooltipLines);
        var quantity = tooltip.ItemQuantity >= 0
            ? tooltip.ItemQuantity
            : liveItem.Value.Rarity == EntityListReader.EntityRarity.Normal ? 0 : -1;
        context.Observe("invitation roll",
            $"roll={roll} rarity={liveItem.Value.Rarity} quantity={quantity}% " +
            $"forbidden=[{string.Join(" | ", tooltip.ForbiddenLines)}] " +
            $"stats=[{string.Join(',', liveItem.Value.Stats.Select(x => $"{x.Id}:{x.Value}"))}] " +
            $"tooltip=[{string.Join(" || ", hover.TooltipLines)}]");
        return (tooltip.IsTheFormed && quantity >= 0, tooltip, quantity);
    }

    private static async Task CancelCurrencyAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        if (context.Snapshot().Cursor.Action != CursorView.CursorAction.UseItem) return;
        await context.VerifiedTapKeyAsync(
            0x1B, ClickIntent.InteractUi, "cancel selected rolling currency",
            () => context.Snapshot().Cursor.Action == CursorView.CursorAction.Free,
            2_000, cancellationToken);
    }

    private static int CurrencyCount(InventoryView inventory, string path)
        => inventory.Items.Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Sum(x => Math.Max(1, x.StackSize));

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

    private static string ItemFingerprint(MapReceptacleView.StagedItem item)
        => $"{item.ItemEntity:X}:{item.Rarity}:{string.Join(',', item.Stats.Select(x => $"{x.Id}:{x.Value}"))}";

    private static string FormatItem(MapReceptacleView.StagedItem item)
        => $"base='{item.BaseName}' rarity={item.Rarity} path='{item.Path}' stats=[{string.Join(',', item.Stats.Select(x => $"{x.Id}:{x.Value}"))}]";
}
