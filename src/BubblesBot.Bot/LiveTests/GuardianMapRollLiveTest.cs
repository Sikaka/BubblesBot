using BubblesBot.Bot.Input;
using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Inspects every identified carried guardian map, then economically rolls only Normal maps or
/// maps with a forbidden modifier. The complete bounded currency reserve is required up front.
/// </summary>
public sealed class GuardianMapRollLiveTest : ILiveTestCase
{
    private const string GuardianMapPath = "Metadata/Items/Maps/MapKeyShaperGuardian";
    private const string AlchemyPath = "Metadata/Items/Currency/CurrencyUpgradeToRare";
    private const string ChaosPath = "Metadata/Items/Currency/CurrencyRerollRare";
    private const string ScouringPath = "Metadata/Items/Currency/CurrencyConvertToNormal";
    private const int MaxChaosPerMap = 10;

    public string Id => "G-07-guardian-map-roll";
    public string Name => "Roll carried Shaper Guardian maps safely";
    public string Description => "Records semantic modifier identities and item quantity, then uses Scour+Alchemy or Chaos until every map is runnable. Research phase forces three Chaos samples on one map before restoring safety.";
    public string ManualSetup => "Open inventory with identified guardian maps plus the complete Scour/Alchemy/Chaos reserve visible, hold no item, and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Economic;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        var maps = GuardianMaps(before.Inventory);
        context.Check(before.Inventory.IsOpen, "inventory open", $"items={before.Inventory.Items.Count}");
        context.Check(before.Cursor.Action == CursorView.CursorAction.Free,
            "cursor free", before.Cursor.Action.ToString());
        context.Check(maps.Length > 0, "carried guardian maps", $"count={maps.Length}");
        if (!before.Inventory.IsOpen || before.Cursor.Action != CursorView.CursorAction.Free
            || maps.Length == 0)
            return LiveTestCaseResult.Blocked("guardian-map rolling setup is incomplete", "PreparedStateMismatch");

        var unidentified = maps.Count(x => !x.Identified);
        if (unidentified > 0)
        {
            context.Observe("guardian identification required", $"unidentified={unidentified}");
            return LiveTestCaseResult.Blocked(
                $"{unidentified} guardian maps must be identified by G-06 first",
                "GuardianMapsNeedIdentification");
        }
        context.Check(true, "all guardian maps identified", "unidentified=0");

        var states = new List<MapRollState>(maps.Length);
        for (var i = 0; i < maps.Length; i++)
        {
            var state = await InspectAsync(context, maps[i].Rect!.Value, $"preflight {i + 1}/{maps.Length}", cancellationToken);
            if (!state.Valid)
                return LiveTestCaseResult.Fail(
                    $"guardian map {i + 1}/{maps.Length} identity or tooltip was unreadable",
                    "TooltipMismatch");
            if (state.Rarity is not EntityListReader.EntityRarity.Normal
                and not EntityListReader.EntityRarity.Rare)
                return LiveTestCaseResult.Blocked(
                    $"{state.MapName} has unsupported rarity {state.Rarity}",
                    "UnsupportedRarity");
            states.Add(state);
        }

        context.Check(true, "guardian identity model",
            "generic consumable keys; the Atlas node supplies Phoenix/Minotaur/Chimera/Hydra identity");

        var rollTargets = states.Where(x =>
                x.Rarity == EntityListReader.EntityRarity.Normal || x.Tooltip.HasForbiddenModifier)
            .ToList();
        ElementGeometry.Rect? researchSampleSlot = null;
        if (context.Phase == LiveTestPhase.Research)
        {
            researchSampleSlot = states[0].Slot;
            if (!rollTargets.Any(x => SameSlot(x.Slot, researchSampleSlot.Value)))
                rollTargets.Add(states[0]);
            context.Observe("research sampling",
                "forcing three Chaos rolls on the first guardian-map slot, then restoring a runnable roll");
        }
        var initialAlchemy = rollTargets.Count(x => x.Rarity == EntityListReader.EntityRarity.Normal);
        var requiredRerollAttempts = rollTargets.Count * MaxChaosPerMap;
        var alchemy = CurrencyCount(before.Inventory, AlchemyPath);
        var chaos = CurrencyCount(before.Inventory, ChaosPath);
        var scouring = CurrencyCount(before.Inventory, ScouringPath);
        var fallbackPairs = Math.Min(scouring, Math.Max(0, alchemy - initialAlchemy));
        var totalRerollBudget = chaos + fallbackPairs;
        context.Observe("guardian rolling preflight",
            $"maps={maps.Length} rollTargets={rollTargets.Count} alchemy={alchemy} " +
            $"scouring={scouring} chaos={chaos} initialAlchemy={initialAlchemy} " +
            $"rerollBudget={totalRerollBudget}/{requiredRerollAttempts} maxPerMap={MaxChaosPerMap}");
        if (alchemy < initialAlchemy)
            return LiveTestCaseResult.Blocked(
                $"need {initialAlchemy} visible Orbs of Alchemy before starting; found {alchemy}",
                "InsufficientAlchemyReserve");
        if (totalRerollBudget < requiredRerollAttempts)
            return LiveTestCaseResult.Blocked(
                $"need {requiredRerollAttempts} total Chaos or Scour+Alchemy attempts; found {totalRerollBudget}",
                "InsufficientRerollReserve");
        context.Check(alchemy >= initialAlchemy, "initial Alchemy reserve", $"{alchemy}/{initialAlchemy}");
        context.Check(totalRerollBudget >= requiredRerollAttempts, "complete mixed reroll reserve",
            $"chaos={chaos} scourAlchemyPairs={fallbackPairs} total={totalRerollBudget}/{requiredRerollAttempts}");

        var alchemyUsed = 0;
        var chaosUsed = 0;
        var scouringUsed = 0;
        foreach (var initial in rollTargets)
        {
            var current = initial;
            var forcedChaosSamples = researchSampleSlot is { } sample
                && SameSlot(initial.Slot, sample) ? 3 : 0;
            if (current.Rarity == EntityListReader.EntityRarity.Normal)
            {
                if (!await ApplyCurrencyAsync(
                        context, current.Slot, AlchemyPath, "Orb of Alchemy", cancellationToken))
                    return LiveTestCaseResult.Fail(
                        $"Alchemy application to {current.MapName} was not positively confirmed",
                        "AlchemyApplyFailed");
                alchemyUsed++;
                current = await InspectAsync(context, current.Slot, "after Alchemy", cancellationToken);
                if (!current.Valid)
                    return LiveTestCaseResult.Fail(
                        "Alchemy result tooltip was unreadable", "TooltipMismatch");
            }

            // Do not carry an Alchemy selection into a Chaos loop.
            await CancelCurrencyAsync(context, cancellationToken);

            var attempts = 0;
            while ((current.Tooltip.HasForbiddenModifier || attempts < forcedChaosSamples)
                && attempts < MaxChaosPerMap)
            {
                // Prefer Scour+Alchemy for map practice so the rarer Chaos reserve remains
                // available for The Formed invitation's quantity target.
                var live = context.Snapshot().Inventory;
                if (attempts >= forcedChaosSamples
                    && CurrencyCount(live, ScouringPath) > 0
                    && CurrencyCount(live, AlchemyPath) > 0)
                {
                    await CancelCurrencyAsync(context, cancellationToken);
                    if (!await ApplyCurrencyAsync(
                            context, current.Slot, ScouringPath, "Orb of Scouring", cancellationToken))
                        return LiveTestCaseResult.Fail(
                            $"Scouring application to {current.MapName} was not positively confirmed",
                            "ScouringApplyFailed");
                    scouringUsed++;
                    await CancelCurrencyAsync(context, cancellationToken);
                    if (!await ApplyCurrencyAsync(
                            context, current.Slot, AlchemyPath, "Orb of Alchemy", cancellationToken))
                        return LiveTestCaseResult.Fail(
                            $"Alchemy application to {current.MapName} was not positively confirmed",
                            "AlchemyApplyFailed");
                    alchemyUsed++;
                }
                else
                {
                    if (!await ApplyCurrencyAsync(
                            context, current.Slot, ChaosPath, "Chaos Orb", cancellationToken))
                        return LiveTestCaseResult.Fail(
                            $"Chaos application to {current.MapName} was not positively confirmed",
                            "ChaosApplyFailed");
                    chaosUsed++;
                }
                attempts++;
                current = await InspectAsync(
                    context, current.Slot, $"after Chaos {attempts}", cancellationToken);
                if (!current.Valid)
                    return LiveTestCaseResult.Fail(
                        "Chaos result tooltip was unreadable", "TooltipMismatch");
            }

            context.Check(!current.Tooltip.HasForbiddenModifier,
                $"{current.MapName} final modifiers",
                current.Tooltip.HasForbiddenModifier
                    ? string.Join(" | ", current.Tooltip.ForbiddenLines)
                    : $"safe at {current.Quantity}% item quantity");
            if (current.Tooltip.HasForbiddenModifier)
                return LiveTestCaseResult.Fail(
                    $"{current.MapName} remains forbidden after {MaxChaosPerMap} Chaos Orbs",
                    "RerollBudgetExhausted");
        }
        await CancelCurrencyAsync(context, cancellationToken);

        var finalStates = new List<MapRollState>(maps.Length);
        for (var i = 0; i < maps.Length; i++)
        {
            var state = await InspectAsync(context, maps[i].Rect!.Value, $"final {i + 1}/{maps.Length}", cancellationToken);
            if (!state.Valid)
            {
                context.Observe("guardian final re-hover", $"map={i + 1}/{maps.Length} first tooltip read was transient");
                await Task.Delay(150, cancellationToken);
                state = await InspectAsync(context, maps[i].Rect!.Value,
                    $"final retry {i + 1}/{maps.Length}", cancellationToken);
            }
            if (!state.Valid || state.Tooltip.HasForbiddenModifier)
                return LiveTestCaseResult.Fail(
                    $"final guardian map {i + 1}/{maps.Length} is unreadable or unsafe",
                    "FinalMapSafetyFailed");
            finalStates.Add(state);
        }

        context.Observe("guardian map quantities", string.Join(" | ", finalStates.Select(x =>
            $"{x.MapName}={x.Quantity}%")));
        return LiveTestCaseResult.Pass(
            $"{finalStates.Count} guardian maps are identified and runnable; Scouring={scouringUsed}, Alchemy={alchemyUsed}, Chaos={chaosUsed}",
            "GuardianMapsRolledSafe");
    }

    private static async Task<MapRollState> InspectAsync(
        LiveTestContext context,
        ElementGeometry.Rect slot,
        string label,
        CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var target = FindGuardianAtSlot(snapshot.Inventory, slot);
        if (target.ItemEntity == 0 || target.Rect is not { } rect)
            return default;
        var point = snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 220, cancellationToken);

        var hovered = context.Snapshot();
        var live = FindGuardianAtSlot(hovered.Inventory, slot);
        var hover = UiHoverView.Read(hovered.Reader, hovered.IngameStateAddress);
        if (live.ItemEntity == 0 || !HoverOwns(hovered, hover.Element, live.ElementAddress)
            || hover.TooltipLines.Count == 0)
            return default;

        var mapName = ResolveMapName(live.BaseName, hover.TooltipLines);
        if (mapName.Length == 0) mapName = "Shaper Guardian Map";
        var tooltip = GuardianInvitationPolicy.EvaluateTooltip(hover.TooltipLines);
        var quantity = tooltip.ItemQuantity >= 0
            ? tooltip.ItemQuantity
            : live.Rarity == EntityListReader.EntityRarity.Normal ? 0 : -1;
        var stats = live.Stats ?? [];
        var catalog = MapStatCatalog.Evaluate(stats);
        var semantic = MapModifierCatalog.EvaluateTooltip(hover.TooltipLines);
        context.Observe("guardian map roll state",
            $"stage='{label}' map='{mapName}' rarity={live.Rarity} identified={live.Identified} " +
            $"quantity={quantity}% forbidden=[{string.Join(" | ", tooltip.ForbiddenLines)}] " +
            $"semantic=[{string.Join(',', semantic.Matches.Select(x => $"{x.Definition.Key}:{x.Disposition}"))}] " +
            $"catalogVetoIds=[{string.Join(',', catalog.VetoHits)}] " +
            $"stats=[{string.Join(',', stats.Select(x => $"{x.Id}:{x.Value}"))}] " +
            $"tooltip=[{string.Join(" || ", hover.TooltipLines)}]");
        return new MapRollState(
            live.Identified && quantity >= 0,
            slot, mapName, live.Rarity, quantity, tooltip);
    }

    private static async Task<bool> ApplyCurrencyAsync(
        LiveTestContext context,
        ElementGeometry.Rect targetSlot,
        string currencyPath,
        string currencyName,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        var target = FindGuardianAtSlot(before.Inventory, targetSlot);
        if (target.ItemEntity == 0 || target.Rect is not { } targetRect)
            return false;

        // Retain a Shift-selected currency across applications when the client exposes it.
        // If the previous application released it, positively select the requested source again.
        if (before.Cursor.Action != CursorView.CursorAction.UseItem
            && !await SelectCurrencyAsync(context, currencyPath, currencyName, cancellationToken))
            return false;

        before = context.Snapshot();
        target = FindGuardianAtSlot(before.Inventory, targetSlot);
        if (target.ItemEntity == 0 || target.Rect is not { } refreshedTargetRect)
            return false;
        targetRect = refreshedTargetRect;
        var currencyBefore = CurrencyCount(before.Inventory, currencyPath);
        var itemBefore = ItemFingerprint(target);

        var targetPoint = before.Window.ToScreen(targetRect.CenterX, targetRect.CenterY);
        await context.HoverAsync(targetPoint.X, targetPoint.Y, 180, cancellationToken);
        var hovered = context.Snapshot();
        var liveTarget = FindGuardianAtSlot(hovered.Inventory, targetSlot);
        var targetHover = UiHoverView.Read(hovered.Reader, hovered.IngameStateAddress);
        if (liveTarget.ItemEntity == 0
            || !HoverOwns(hovered, targetHover.Element, liveTarget.ElementAddress))
        {
            context.Check(false, $"{currencyName} target identity",
                $"target=0x{(long)liveTarget.ElementAddress:X} hover=0x{(long)targetHover.Element:X}");
            await CancelCurrencyAsync(context, cancellationToken);
            return false;
        }
        context.Check(true, $"{currencyName} target identity",
            $"slot=({targetSlot.CenterX:F0},{targetSlot.CenterY:F0}) entity=0x{(long)liveTarget.ItemEntity:X} base='{liveTarget.BaseName}'");

        var apply = await context.VerifiedModifierClickAsync(
            targetPoint.X, targetPoint.Y, [0x10], ClickIntent.InteractUi,
            $"Shift-apply {currencyName} to Shaper Guardian map",
            () =>
            {
                var current = context.Snapshot();
                var currentTarget = FindGuardianAtSlot(current.Inventory, targetSlot);
                return currentTarget.ItemEntity != 0
                    && CurrencyCount(current.Inventory, currencyPath) == currencyBefore - 1
                    && ItemFingerprint(currentTarget) != itemBefore;
            }, 3_000, cancellationToken);
        if (apply != ActionOutcome.Confirmed)
        {
            await CancelCurrencyAsync(context, cancellationToken);
            return false;
        }

        return await context.WaitForInputIdleAsync(
            $"after applying {currencyName}", 1_500, cancellationToken);
    }

    private static async Task<bool> SelectCurrencyAsync(
        LiveTestContext context,
        string currencyPath,
        string currencyName,
        CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        var currency = before.Inventory.Items
            .Where(x => x.Path.Equals(currencyPath, StringComparison.OrdinalIgnoreCase)
                && x.Rect is not null)
            .OrderByDescending(x => x.StackSize)
            .FirstOrDefault();
        if (currency.ItemEntity == 0 || currency.Rect is not { } currencyRect)
            return false;

        var currencyBefore = CurrencyCount(before.Inventory, currencyPath);
        var sourcePoint = before.Window.ToScreen(currencyRect.CenterX, currencyRect.CenterY);
        await context.HoverAsync(sourcePoint.X, sourcePoint.Y, 180, cancellationToken);
        var sourceSnapshot = context.Snapshot();
        var sourceHover = UiHoverView.Read(sourceSnapshot.Reader, sourceSnapshot.IngameStateAddress);
        if (!HoverOwns(sourceSnapshot, sourceHover.Element, currency.ElementAddress)
            || !sourceHover.TooltipLines.Any(x =>
                x.Contains(currencyName, StringComparison.OrdinalIgnoreCase)))
        {
            context.Check(false, $"{currencyName} source identity",
                string.Join(" | ", sourceHover.TooltipLines));
            return false;
        }
        context.Check(true, $"{currencyName} source identity",
            $"element=0x{(long)currency.ElementAddress:X} stackTotal={currencyBefore}");

        var use = await context.VerifiedRightClickAsync(
            sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi, $"select {currencyName}",
            () => context.Snapshot().Cursor.Action == CursorView.CursorAction.UseItem,
            2_000, cancellationToken);
        if (use != ActionOutcome.Confirmed) return false;
        return await context.WaitForInputIdleAsync(
            $"after selecting {currencyName}", 1_500, cancellationToken);
    }

    private static InventoryView.Item[] GuardianMaps(InventoryView inventory)
        => inventory.Items.Where(x =>
                x.Path.Equals(GuardianMapPath, StringComparison.OrdinalIgnoreCase)
                && x.Rect is not null)
            .OrderBy(x => x.Rect!.Value.Y)
            .ThenBy(x => x.Rect!.Value.X)
            .ToArray();

    private static string ResolveMapName(string baseName, IEnumerable<string> tooltipLines)
        => GuardianRotationPolicy.Maps.FirstOrDefault(map =>
            baseName.Contains(map, StringComparison.OrdinalIgnoreCase)
            || tooltipLines.Any(line => line.Contains(map, StringComparison.OrdinalIgnoreCase)))
            ?? string.Empty;

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

    private static bool SameSlot(ElementGeometry.Rect left, ElementGeometry.Rect right)
        => Math.Abs(left.CenterX - right.CenterX) < 2f
        && Math.Abs(left.CenterY - right.CenterY) < 2f;

    private static string ItemFingerprint(InventoryView.Item item)
        => $"{item.ItemEntity:X}:{item.Rarity}:{item.Identified}:" +
            string.Join(',', (item.Stats ?? []).Select(x => $"{x.Id}:{x.Value}"));

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
            0x1B, ClickIntent.InteractUi, "cancel selected rolling currency",
            () => context.Snapshot().Cursor.Action == CursorView.CursorAction.Free,
            2_000, cancellationToken);
    }

    private readonly record struct MapRollState(
        bool Valid,
        ElementGeometry.Rect Slot,
        string MapName,
        EntityListReader.EntityRarity Rarity,
        int Quantity,
        GuardianInvitationState Tooltip);
}
