using BubblesBot.Bot.Input;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Proves the contextual Ctrl+click transfer contract in both directions without consuming
/// an item: inventory -> the currently visible normal stash tab -> inventory.
/// </summary>
public sealed class StashCtrlClickRoundTripLiveTest : ILiveTestCase
{
    private const int LeftControlVk = 0xA2;

    public string Id => "H-06-stash-ctrl-roundtrip";
    public string Name => "Inventory/stash Ctrl+click round trip";
    public string Description => "Selects one unique non-stackable equipment item, proves exact hover/tooltip identity and both container deltas, then restores the original item multisets and visible stash tab.";
    public string ManualSetup => "Open an empty normal stash tab and the inventory, leave nothing hovered, and carry at least one uniquely pathed non-stackable weapon or armour item. The test does not switch tabs.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var inventory = baseline.Inventory;
        var stash = baseline.StashInventory;
        context.Check(inventory.IsOpen, "inventory prepared", $"open={inventory.IsOpen} items={inventory.Items.Count}");
        context.Check(stash.IsOpen, "stash prepared", $"open={stash.IsOpen} tabIndex={stash.VisibleTabIndex} totalTabs={stash.TotalTabs}");
        context.Check(stash.VisibleTabIndex >= 0, "visible stash tab readable", $"index={stash.VisibleTabIndex}");
        context.Check(stash.Items.Count == 0, "visible stash tab empty", $"items={stash.Items.Count}");
        if (!inventory.IsOpen || !stash.IsOpen || stash.VisibleTabIndex < 0 || stash.Items.Count != 0)
            return LiveTestCaseResult.Blocked("requires a readable inventory and an empty normal visible stash tab", "PreparedStateMismatch");

        var targetCandidates = inventory.Items
            .Where(x => x.StackSize == 1
                && x.Rect is { Width: > 0, Height: > 0 }
                && (x.Path.StartsWith("Metadata/Items/Weapons/", StringComparison.Ordinal)
                    || x.Path.StartsWith("Metadata/Items/Armours/", StringComparison.Ordinal)))
            .Where(x => inventory.Items.Count(other => string.Equals(other.Path, x.Path, StringComparison.Ordinal)) == 1)
            .OrderBy(x => x.OccupiedCells)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();
        context.Check(targetCandidates.Length > 0, "unique equipment transfer candidate",
            targetCandidates.Length == 0 ? "none" : string.Join(", ", targetCandidates.Select(x => x.Path)));
        if (targetCandidates.Length == 0)
            return LiveTestCaseResult.Blocked("no uniquely pathed non-stackable weapon or armour is available", "NoSafeTransferCandidate");

        var target = targetCandidates[0];
        var targetBaseName = ReadBaseName(baseline.Reader, target.ItemEntity);
        context.Check(!string.IsNullOrWhiteSpace(targetBaseName), "target base identity",
            $"base='{targetBaseName}' metadata='{target.Path}' entity=0x{(long)target.ItemEntity:X}");
        if (string.IsNullOrWhiteSpace(targetBaseName) || target.Rect is not { } inventoryRect)
            return LiveTestCaseResult.Blocked("candidate base identity or geometry is unreadable", "CandidateUnreadable");

        var tabIndex = stash.VisibleTabIndex;
        var totalTabs = stash.TotalTabs;
        var inventoryBefore = InventoryFingerprint(inventory);
        var stashBefore = StashFingerprint(stash);
        var inventoryPathBefore = CountPath(inventory, target.Path);
        var stashPathBefore = CountPath(stash, target.Path);
        context.Observe("transfer baseline",
            $"tabIndex={tabIndex}/{totalTabs} target='{targetBaseName}' metadata='{target.Path}' inventory=[{inventoryBefore}] stash=[{stashBefore}]");

        if (!await VerifyInventoryHoverAsync(context, target, targetBaseName, "inventory source", cancellationToken))
            return LiveTestCaseResult.Fail("inventory target did not resolve through UIHover with a rendered tooltip", "TooltipMismatch");

        var inventoryPoint = context.Snapshot().Window.ToScreen(inventoryRect.CenterX, inventoryRect.CenterY);
        var deposit = await context.VerifiedModifierClickAsync(
            inventoryPoint.X, inventoryPoint.Y,
            [LeftControlVk], ClickIntent.InteractUi,
            $"Ctrl+click inventory -> stash '{targetBaseName}'",
            () =>
            {
                var current = context.Snapshot();
                return current.StashInventory.IsOpen
                    && current.StashInventory.VisibleTabIndex == tabIndex
                    && CountPath(current.Inventory, target.Path) == inventoryPathBefore - 1
                    && CountPath(current.StashInventory, target.Path) == stashPathBefore + 1;
            },
            4_000, cancellationToken);
        if (!await context.WaitForInputIdleAsync("after inventory-to-stash transfer", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, tabIndex,
                inventoryBefore, stashBefore, "input did not settle after deposit", "InputSettleFailed", cancellationToken);
        if (deposit != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, tabIndex,
                inventoryBefore, stashBefore, "deposit lacked exact source/destination deltas", "TransferOutcomeMismatch", cancellationToken);

        var deposited = context.Snapshot();
        var stashMatches = deposited.StashInventory.Items
            .Where(x => string.Equals(x.Path, target.Path, StringComparison.Ordinal)).ToArray();
        context.Check(deposited.StashInventory.VisibleTabIndex == tabIndex, "deposit tab stability",
            $"before={tabIndex} after={deposited.StashInventory.VisibleTabIndex}");
        context.Check(stashMatches.Length == 1, "exact stash addition",
            $"metadata='{target.Path}' matches={stashMatches.Length} inventoryCount={CountPath(deposited.Inventory, target.Path)} stashCount={CountPath(deposited.StashInventory, target.Path)}");
        var depositedBaseName = stashMatches.Length == 1
            ? ReadBaseName(deposited.Reader, stashMatches[0].ItemEntity)
            : string.Empty;
        context.Check(stashMatches.Length == 1
                && string.Equals(depositedBaseName, targetBaseName, StringComparison.Ordinal)
                && stashMatches[0].StackSize == target.StackSize
                && stashMatches[0].Width == target.Width
                && stashMatches[0].Height == target.Height,
            "durable transferred item identity",
            stashMatches.Length == 1
                ? $"base='{depositedBaseName}' metadata='{stashMatches[0].Path}' stack={stashMatches[0].StackSize} size={stashMatches[0].Width}x{stashMatches[0].Height}"
                : "stash target missing");
        context.Observe("container entity rematerialization",
            stashMatches.Length == 1
                ? $"inventoryEntity=0x{(long)target.ItemEntity:X} stashEntity=0x{(long)stashMatches[0].ItemEntity:X}; pointer continuity is not a valid transfer oracle"
                : "destination entity unavailable");
        if (stashMatches.Length != 1 || stashMatches[0].Rect is not { })
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, tabIndex,
                inventoryBefore, stashBefore, "deposited item did not resolve uniquely in the visible tab", "DestinationUnreadable", cancellationToken);

        if (!await VerifyStashHoverAsync(context, stashMatches[0], targetBaseName, "stash source", cancellationToken))
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, tabIndex,
                inventoryBefore, stashBefore, "stash target did not resolve through UIHover with a rendered tooltip", "TooltipMismatch", cancellationToken);

        var liveStashTarget = context.Snapshot().StashInventory.Items.Single(x =>
            string.Equals(x.Path, target.Path, StringComparison.Ordinal));
        if (liveStashTarget.Rect is not { } stashRect)
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, tabIndex,
                inventoryBefore, stashBefore, "stash target geometry changed before restore", "DestinationUnreadable", cancellationToken);
        var stashPoint = context.Snapshot().Window.ToScreen(stashRect.CenterX, stashRect.CenterY);
        var withdraw = await context.VerifiedModifierClickAsync(
            stashPoint.X, stashPoint.Y,
            [LeftControlVk], ClickIntent.InteractUi,
            $"Ctrl+click stash -> inventory '{targetBaseName}'",
            () =>
            {
                var current = context.Snapshot();
                return current.StashInventory.IsOpen
                    && current.StashInventory.VisibleTabIndex == tabIndex
                    && CountPath(current.Inventory, target.Path) == inventoryPathBefore
                    && CountPath(current.StashInventory, target.Path) == stashPathBefore;
            },
            4_000, cancellationToken);
        if (withdraw != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("withdrawal lacked exact source/destination deltas; automatic recovery could not be proven", "RestoreFailed");
        if (!await context.WaitForInputIdleAsync("after stash-to-inventory restore", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after withdrawal", "InputSettleFailed");

        await MoveToNeutralAsync(context, cancellationToken);
        var final = context.Snapshot();
        var inventoryAfter = InventoryFingerprint(final.Inventory);
        var stashAfter = StashFingerprint(final.StashInventory);
        context.Check(final.Inventory.IsOpen && final.StashInventory.IsOpen, "panels remain open",
            $"inventory={final.Inventory.IsOpen} stash={final.StashInventory.IsOpen}");
        context.Check(final.StashInventory.VisibleTabIndex == tabIndex
                && final.StashInventory.TotalTabs == totalTabs,
            "visible stash tab restored",
            $"before={tabIndex}/{totalTabs} after={final.StashInventory.VisibleTabIndex}/{final.StashInventory.TotalTabs}");
        context.Check(string.Equals(inventoryAfter, inventoryBefore, StringComparison.Ordinal),
            "inventory fingerprint restored", $"before=[{inventoryBefore}] after=[{inventoryAfter}]");
        context.Check(string.Equals(stashAfter, stashBefore, StringComparison.Ordinal),
            "stash fingerprint restored", $"before=[{stashBefore}] after=[{stashAfter}]");
        context.Check(!IsAnyItemHovered(final), "item hover cleared", "no inventory or stash item resolves through UIHover");

        if (!final.Inventory.IsOpen || !final.StashInventory.IsOpen
            || final.StashInventory.VisibleTabIndex != tabIndex
            || !string.Equals(inventoryAfter, inventoryBefore, StringComparison.Ordinal)
            || !string.Equals(stashAfter, stashBefore, StringComparison.Ordinal)
            || IsAnyItemHovered(final))
            return LiveTestCaseResult.Fail("container state or hover did not exactly restore", "RestoreFailed");

        return LiveTestCaseResult.Pass(
            $"proved Ctrl+click '{targetBaseName}' inventory -> stash -> inventory with exact identities, deltas, tab stability, and full restoration",
            "CompletedAndRestored");
    }

    private static async Task<LiveTestCaseResult> FailWithRecoveryAsync(
        LiveTestContext context,
        string path,
        string baseName,
        int tabIndex,
        string inventoryBefore,
        string stashBefore,
        string failure,
        string classification,
        CancellationToken cancellationToken)
    {
        var current = context.Snapshot();
        var matches = current.StashInventory.Items
            .Where(x => string.Equals(x.Path, path, StringComparison.Ordinal) && x.Rect is not null).ToArray();
        if (current.StashInventory.IsOpen
            && current.StashInventory.VisibleTabIndex == tabIndex
            && matches.Length == 1
            && matches[0].Rect is { } rect)
        {
            await VerifyStashHoverAsync(context, matches[0], baseName, "recovery stash source", cancellationToken);
            var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
            await context.VerifiedModifierClickAsync(
                point.X, point.Y, [LeftControlVk], ClickIntent.InteractUi,
                $"recovery Ctrl+click stash -> inventory '{baseName}'",
                () =>
                {
                    var recovered = context.Snapshot();
                    return string.Equals(InventoryFingerprint(recovered.Inventory), inventoryBefore, StringComparison.Ordinal)
                        && string.Equals(StashFingerprint(recovered.StashInventory), stashBefore, StringComparison.Ordinal);
                }, 4_000, cancellationToken);
            await context.WaitForInputIdleAsync("after recovery withdrawal", 1_500, cancellationToken);
        }
        await MoveToNeutralAsync(context, cancellationToken);
        var final = context.Snapshot();
        var restored = string.Equals(InventoryFingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal)
            && string.Equals(StashFingerprint(final.StashInventory), stashBefore, StringComparison.Ordinal)
            && final.StashInventory.VisibleTabIndex == tabIndex;
        context.Check(restored, "failure-path restoration", restored ? "baseline restored" : "baseline mismatch");
        return LiveTestCaseResult.Fail(
            restored ? $"{failure}; starting state was restored" : $"{failure}; starting state was not restored",
            restored ? classification : "RestoreFailed");
    }

    private static async Task<bool> VerifyInventoryHoverAsync(
        LiveTestContext context,
        InventoryView.Item target,
        string baseName,
        string label,
        CancellationToken cancellationToken)
    {
        if (target.Rect is not { } rect) return false;
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 120, cancellationToken);
        var reached = await context.WaitUntilAsync(label,
            () => ResolvesHoverTo(context.Snapshot(), target.ElementAddress), 1_500, cancellationToken, 20);
        var tooltip = ReadTooltip(context.Snapshot(), target.ElementAddress);
        context.Check(reached, $"{label} exact UIHover",
            $"metadata='{target.Path}' element=0x{(long)target.ElementAddress:X}");
        context.Check(tooltip.Root != 0 && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase)),
            $"{label} rendered tooltip",
            $"root=0x{(long)tooltip.Root:X} base='{baseName}' lines={tooltip.Lines.Count}");
        return reached && tooltip.Root != 0
            && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> VerifyStashHoverAsync(
        LiveTestContext context,
        StashInventoryView.Item target,
        string baseName,
        string label,
        CancellationToken cancellationToken)
    {
        if (target.Rect is not { } rect) return false;
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 120, cancellationToken);
        var reached = await context.WaitUntilAsync(label,
            () => ResolvesHoverTo(context.Snapshot(), target.ElementAddress), 1_500, cancellationToken, 20);
        var tooltip = ReadTooltip(context.Snapshot(), target.ElementAddress);
        context.Check(reached, $"{label} exact UIHover",
            $"metadata='{target.Path}' element=0x{(long)target.ElementAddress:X}");
        context.Check(tooltip.Root != 0 && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase)),
            $"{label} rendered tooltip",
            $"root=0x{(long)tooltip.Root:X} base='{baseName}' lines={tooltip.Lines.Count}");
        return reached && tooltip.Root != 0
            && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ResolvesHoverTo(GameSnapshot snapshot, nint target)
    {
        snapshot.Reader.TryReadStruct<nint>(
            snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var current);
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

    private static bool IsAnyItemHovered(GameSnapshot snapshot)
        => snapshot.Inventory.Items.Any(x => ResolvesHoverTo(snapshot, x.ElementAddress))
            || snapshot.StashInventory.Items.Any(x => ResolvesHoverTo(snapshot, x.ElementAddress));

    private static async Task MoveToNeutralAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var window = context.Snapshot().Window;
        await context.HoverAsync(window.OriginX + window.Width / 2, window.OriginY + 80, 150, cancellationToken);
    }

    private static (nint Root, IReadOnlyList<string> Lines) ReadTooltip(GameSnapshot snapshot, nint element)
    {
        snapshot.Reader.TryReadStruct<nint>(element + KnownOffsets.Element.RenderedTooltip, out var root);
        if (root == 0) return (0, []);
        var lines = new List<string>();
        var seen = new HashSet<nint>();
        var queue = new Queue<(nint Address, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 1_024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var elementSnapshot = ElementReader.TryReadSnapshot(snapshot.Reader, address, 128);
            if (elementSnapshot is null) continue;
            if (ElementReader.IsVisibleDeep(snapshot.Reader, address))
            {
                var text = NativeString.Read(snapshot.Reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text))
                    text = NativeString.Read(snapshot.Reader, address + KnownOffsets.Element.Text);
                foreach (var line in text.Split(['\r', '\n'],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            if (depth >= 14) continue;
            foreach (var child in elementSnapshot.Children) queue.Enqueue((child, depth + 1));
        }
        return (root, lines);
    }

    private static string ReadBaseName(MemoryReader reader, nint itemEntity)
    {
        var components = EntityComponents.ReadComponentMap(reader, itemEntity);
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static int CountPath(InventoryView inventory, string path)
        => inventory.Items.Count(x => string.Equals(x.Path, path, StringComparison.Ordinal));

    private static int CountPath(StashInventoryView stash, string path)
        => stash.Items.Count(x => string.Equals(x.Path, path, StringComparison.Ordinal));

    private static string InventoryFingerprint(InventoryView inventory)
        => string.Join('|', inventory.Items.OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string StashFingerprint(StashInventoryView stash)
        => string.Join('|', stash.Items.OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";
}
