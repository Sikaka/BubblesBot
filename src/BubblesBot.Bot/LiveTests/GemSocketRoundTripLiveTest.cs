using BubblesBot.Bot.Input;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Reversible loose gem -> exact compatible socket -> loose gem round trip.</summary>
public sealed class GemSocketRoundTripLiveTest : ILiveTestCase
{
    private const string SwordPath = "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1";
    private const string GemPath = "Metadata/Items/Gems/SkillGemSplittingSteel";

    public string Id => "A-07-gem-socket-roundtrip";
    public string Name => "Exact green-socket gem round trip";
    public string Description => "Picks up loose Splitting Steel, clicks the socket overlay's exact green-socket center, proves the socketed state, then removes and restores the gem.";
    public string ManualSetup => "Open inventory with cursor free, one loose Splitting Steel, and the empty unlinked RGB Rusted Sword fixture. Keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var looseGems = baseline.Inventory.Items.Where(x => x.Path == GemPath).ToArray();
        var target = FindSword(baseline, socketed: false);
        var prepared = baseline.Inventory.IsOpen
            && baseline.Cursor.Action == CursorView.CursorAction.Free
            && looseGems.Length == 1
            && looseGems[0].Rect is not null
            && target is not null;
        context.Check(prepared, "prepared gem/socket fixture",
            $"inventory={baseline.Inventory.IsOpen} cursor={baseline.Cursor.Action} looseGems={looseGems.Length} emptyRgbSwords={(target is null ? 0 : 1)}");
        if (!prepared || looseGems[0].Rect is not { } gemRect || target is not { } targetBefore)
            return LiveTestCaseResult.Blocked("loose Splitting Steel and the empty RGB sword fixture are required", "PreparedStateMismatch");

        if (!SkillGemReader.TryRead(baseline.Reader, looseGems[0].ItemEntity, out var looseGem))
            return LiveTestCaseResult.Blocked("loose Splitting Steel state is unreadable", "GemStateUnreadable");
        if (!ItemSocketUiView.TryRead(baseline.Reader, targetBefore.Item, targetBefore.Sockets, out var socketUi))
            return LiveTestCaseResult.Blocked("the dedicated socket overlay is not uniquely readable", "SocketUiUnreadable");

        var greenIndices = targetBefore.Sockets.Colors.Select((color, index) => (color, index))
            .Where(x => x.color == ItemSocketsReader.SocketColor.Green).Select(x => x.index).ToArray();
        if (greenIndices.Length != 1 || socketUi.Targets.Count != targetBefore.Sockets.SocketCount)
            return LiveTestCaseResult.Blocked("fixture does not expose one unambiguous green socket target", "SocketTargetAmbiguous");
        var greenIndex = greenIndices[0];
        var greenTarget = socketUi.Targets[greenIndex];
        var window = baseline.Window;
        var gemPoint = window.ToScreen(gemRect.CenterX, gemRect.CenterY);
        var greenPoint = window.ToScreen(greenTarget.CenterX, greenTarget.CenterY);
        var inventoryBefore = PositionalFingerprint(baseline.Inventory);
        var identitiesBefore = IdentityMultiset(baseline);
        var swordIdentityBefore = ItemIdentityFingerprint.Capture(baseline.Reader, targetBefore.Item);
        var inventoryWithoutLooseGem = PositionalFingerprint(baseline.Inventory.Items
            .Where(x => x.ElementAddress != looseGems[0].ElementAddress));
        var unchangedIdentities = IdentityMultiset(baseline, looseGems[0].ElementAddress, targetBefore.Item.ElementAddress);

        context.Check(looseGem.MetadataPath == GemPath && !string.IsNullOrWhiteSpace(looseGem.BaseName),
            "loose gem identity", looseGem.Canonical);
        context.Check(greenIndex == 1, "fixture green socket index", $"index={greenIndex} colors={string.Concat(targetBefore.Sockets.Colors.Select(Symbol))}");
        context.Check(greenTarget.AnchorElement != 0,
            "dedicated green socket UI anchor",
            $"item=0x{(long)targetBefore.Item.ElementAddress:X} root=0x{(long)socketUi.SocketRootElement:X} " +
            $"anchor=0x{(long)greenTarget.AnchorElement:X} anchorRect={greenTarget.AnchorRect} click=({greenTarget.CenterX:F2},{greenTarget.CenterY:F2})");
        context.Check(MathF.Abs(greenTarget.CenterY - targetBefore.Item.Rect!.Value.CenterY) < 1f,
            "fixture coordinate coincidence documented",
            $"the middle green socket happens to share the 1x3 item's center at ({greenTarget.CenterX:F2},{greenTarget.CenterY:F2}); " +
            "authorization still came from socket anchor/root geometry, never an item-center fallback");

        var gemTooltip = await HoverAndReadTooltipAsync(context, looseGems[0], "loose Splitting Steel", cancellationToken);
        if (!gemTooltip.Contains(looseGem.BaseName, StringComparison.OrdinalIgnoreCase))
            return LiveTestCaseResult.Fail("loose gem hover/tooltip identity did not match", "GemTooltipMismatch");

        await context.HoverAsync(greenPoint.X, greenPoint.Y, 150, cancellationToken);
        var socketHover = await context.WaitUntilAsync("exact green socket hover",
            () => ResolvesHoverTo(context.Snapshot(), targetBefore.Item.ElementAddress),
            1_500, cancellationToken, 20);
        context.Check(socketHover, "green socket coordinate resolves to owning sword",
            $"layoutAnchor=0x{(long)greenTarget.AnchorElement:X} sword=0x{(long)targetBefore.Item.ElementAddress:X} screen=({greenPoint.X},{greenPoint.Y}); " +
            "socket overlay anchors are layout-only and the live UIHover is their owning item");
        if (!socketHover)
            return LiveTestCaseResult.Fail("green socket coordinate did not resolve to its owning sword", "SocketHoverMismatch");

        var pickup = await context.VerifiedClickAsync(gemPoint.X, gemPoint.Y, ClickIntent.InteractUi,
            $"pick up loose '{looseGem.BaseName}'",
            () =>
            {
                var current = context.Snapshot();
                var emptySword = FindSword(current, socketed: false);
                return current.Cursor.Action == CursorView.CursorAction.HoldItem
                    && !current.Inventory.Items.Any(x => x.Path == GemPath)
                    && PositionalFingerprint(current.Inventory) == inventoryWithoutLooseGem
                    && emptySword?.Identity.Canonical == swordIdentityBefore.Canonical;
            }, 3_000, cancellationToken);
        if (pickup != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "loose gem pickup was not proven", "PickupMismatch", cancellationToken);
        if (!await context.WaitForInputIdleAsync("after loose gem pickup", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "input did not settle after gem pickup", "InputSettleFailed", cancellationToken);

        var socket = await context.VerifiedClickAsync(greenPoint.X, greenPoint.Y, ClickIntent.InteractUi,
            $"place held '{looseGem.BaseName}' into exact green socket index {greenIndex}",
            () => IsExpectedSocketedState(context.Snapshot(), looseGem, greenIndex, inventoryWithoutLooseGem),
            3_000, cancellationToken);
        if (socket != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "exact green-socket placement was not proven", "SocketPlacementMismatch", cancellationToken);
        if (!await context.WaitForInputIdleAsync("after green-socket placement", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "input did not settle after socket placement", "InputSettleFailed", cancellationToken);

        var socketedSnapshot = context.Snapshot();
        var socketedSword = FindSword(socketedSnapshot, socketed: true);
        if (socketedSword is null)
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "socketed sword could not be re-resolved", "SocketIdentityUnreadable", cancellationToken);
        var socketedGem = socketedSword.Value.Sockets.SocketedGems.Single();
        context.Check(socketedGem.SocketIndex == greenIndex, "gem entered intended green socket",
            socketedGem.Canonical);
        context.Check(GemStateEquals(socketedGem, looseGem), "socketed gem state equals loose source",
            $"loose=[{looseGem.Canonical}] socketed=[{socketedGem.Canonical}]");
        context.Check(socketedSword.Value.Sockets.Colors.SequenceEqual(targetBefore.Sockets.Colors)
                && socketedSword.Value.Sockets.LinkGroupSizes.SequenceEqual(targetBefore.Sockets.LinkGroupSizes),
            "socket colors and links unchanged", socketedSword.Value.Sockets.Canonical);
        context.Check(socketedSword.Value.Identity.Canonical != swordIdentityBefore.Canonical,
            "sword identity changed by declared gem delta only",
            $"before=[{swordIdentityBefore.Canonical}] socketed=[{socketedSword.Value.Identity.Canonical}]");
        context.Check(IdentityMultiset(socketedSnapshot, socketedSword.Value.Item.ElementAddress) == unchangedIdentities,
            "all unrelated inventory identities unchanged", unchangedIdentities);
        context.Observe("socket mutation entity behavior",
            $"before=0x{(long)targetBefore.Item.ItemEntity:X} socketed=0x{(long)socketedSword.Value.Item.ItemEntity:X}");

        if (!ItemSocketUiView.TryRead(socketedSnapshot.Reader, socketedSword.Value.Item,
                socketedSword.Value.Sockets, out var socketedUi))
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "socket UI could not be re-resolved after mutation", "SocketUiUnreadable", cancellationToken);
        var removalTarget = socketedUi.Targets[greenIndex];
        var removalPoint = socketedSnapshot.Window.ToScreen(removalTarget.CenterX, removalTarget.CenterY);
        context.Observe("socket UI anchor behavior",
            $"before=0x{(long)greenTarget.AnchorElement:X} socketed=0x{(long)removalTarget.AnchorElement:X}");

        await context.HoverAsync(removalPoint.X, removalPoint.Y, 150, cancellationToken);
        var removalHover = await context.WaitUntilAsync("socketed gem hover",
            () => ResolvesHoverTo(context.Snapshot(), socketedSword.Value.Item.ElementAddress),
            1_500, cancellationToken, 20);
        context.Check(removalHover, "removal coordinate resolves to owning sword",
            $"layoutAnchor=0x{(long)removalTarget.AnchorElement:X} sword=0x{(long)socketedSword.Value.Item.ElementAddress:X} screen=({removalPoint.X},{removalPoint.Y})");
        if (!removalHover)
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "socketed gem anchor hover failed", "SocketHoverMismatch", cancellationToken);

        var remove = await context.VerifiedRightClickAsync(removalPoint.X, removalPoint.Y, ClickIntent.InteractUi,
            $"right-click remove '{looseGem.BaseName}' from exact green socket index {greenIndex}",
            () =>
            {
                var current = context.Snapshot();
                var emptySword = FindSword(current, socketed: false);
                var looseCount = current.Inventory.Items.Count(x => x.Path == GemPath);
                var recognizedOutcome = (current.Cursor.Action == CursorView.CursorAction.HoldItem && looseCount == 0)
                    || (current.Cursor.Action == CursorView.CursorAction.Free && looseCount == 1);
                return recognizedOutcome && emptySword?.Identity.Canonical == swordIdentityBefore.Canonical;
            }, 3_000, cancellationToken);
        if (remove != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "socketed gem removal was not proven", "SocketRemovalMismatch", cancellationToken);
        if (!await context.WaitForInputIdleAsync("after socketed gem removal", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, gemPoint, greenIndex, inventoryBefore, identitiesBefore,
                "input did not settle after gem removal", "InputSettleFailed", cancellationToken);

        var removalState = context.Snapshot();
        context.Observe("right-click removal disposition",
            $"cursor={removalState.Cursor.Action} looseInventoryGems={removalState.Inventory.Items.Count(x => x.Path == GemPath)}");
        if (!await RestoreLooseGemAsync(context, gemPoint, looseGem, inventoryBefore, identitiesBefore, cancellationToken))
            return LiveTestCaseResult.Fail("loose gem original cell was not restored", "RestoreFailed");

        await MoveNeutralAsync(context, cancellationToken);
        var final = context.Snapshot();
        context.Check(IsRestored(final, inventoryBefore, identitiesBefore), "exact fixture restored",
            $"cursor={final.Cursor.Action} inventory=[{PositionalFingerprint(final.Inventory)}]");
        return LiveTestCaseResult.Pass(
            $"socketed '{looseGem.BaseName}' into RGB sword socket {greenIndex} through its dedicated green-socket UI anchor, proved the exact identity delta, then removed and restored it",
            "CompletedAndRestored");
    }

    private static bool IsExpectedSocketedState(
        GameSnapshot snapshot, SkillGemReader.Snapshot expectedGem, int socketIndex, string inventoryWithoutGem)
    {
        var sword = FindSword(snapshot, socketed: true);
        return snapshot.Cursor.Action == CursorView.CursorAction.Free
            && !snapshot.Inventory.Items.Any(x => x.Path == GemPath)
            && PositionalFingerprint(snapshot.Inventory) == inventoryWithoutGem
            && sword is { } value
            && value.Sockets.SocketedGems.Count == 1
            && value.Sockets.SocketedGems[0].SocketIndex == socketIndex
            && GemStateEquals(value.Sockets.SocketedGems[0], expectedGem);
    }

    private static bool IsRestored(GameSnapshot snapshot, string inventoryBefore, string identitiesBefore)
        => snapshot.Cursor.Action == CursorView.CursorAction.Free
            && PositionalFingerprint(snapshot.Inventory) == inventoryBefore
            && IdentityMultiset(snapshot) == identitiesBefore
            && snapshot.Inventory.Items.Count(x => x.Path == GemPath) == 1
            && FindSword(snapshot, socketed: false) is not null;

    private static async Task<LiveTestCaseResult> FailWithRecoveryAsync(
        LiveTestContext context, (int X, int Y) gemPoint, int greenIndex,
        string inventoryBefore, string identitiesBefore, string failure, string classification,
        CancellationToken cancellationToken)
    {
        var state = context.Snapshot();
        var socketed = FindSword(state, socketed: true);
        if (state.Cursor.Action == CursorView.CursorAction.Free && socketed is { } value
            && ItemSocketUiView.TryRead(state.Reader, value.Item, value.Sockets, out var ui)
            && greenIndex < ui.Targets.Count)
        {
            var target = ui.Targets[greenIndex];
            var point = state.Window.ToScreen(target.CenterX, target.CenterY);
            await context.VerifiedRightClickAsync(point.X, point.Y, ClickIntent.InteractUi,
                "recovery right-click remove gem from exact green socket",
                () => FindSword(context.Snapshot(), socketed: false) is not null,
                3_000, cancellationToken);
            await context.WaitForInputIdleAsync("after recovery socket removal", 1_500, cancellationToken);
        }

        var currentGem = context.Snapshot().Inventory.Items.SingleOrDefault(x => x.Path == GemPath);
        var expected = currentGem.ItemEntity != 0 && SkillGemReader.TryRead(context.Snapshot().Reader, currentGem.ItemEntity, out var read)
            ? read
            : new SkillGemReader.Snapshot(GemPath, "Splitting Steel", 1, 0, 0, uint.MaxValue);
        await RestoreLooseGemAsync(context, gemPoint, expected, inventoryBefore, identitiesBefore, cancellationToken);

        var restored = IsRestored(context.Snapshot(), inventoryBefore, identitiesBefore);
        context.Check(restored, "failure-path gem/socket restoration", restored ? "baseline restored" : "baseline mismatch");
        return LiveTestCaseResult.Fail(restored ? failure + "; baseline restored" : failure + "; baseline not restored",
            restored ? classification : "RestoreFailed");
    }

    private static async Task<bool> RestoreLooseGemAsync(
        LiveTestContext context,
        (int X, int Y) originalPoint,
        SkillGemReader.Snapshot expected,
        string inventoryBefore,
        string identitiesBefore,
        CancellationToken cancellationToken)
    {
        var state = context.Snapshot();
        if (state.Cursor.Action == CursorView.CursorAction.HoldItem)
        {
            var restore = await context.VerifiedClickAsync(originalPoint.X, originalPoint.Y, ClickIntent.InteractUi,
                $"restore held '{expected.BaseName}' to exact original inventory cell",
                () => IsRestored(context.Snapshot(), inventoryBefore, identitiesBefore),
                3_000, cancellationToken);
            if (restore != ActionOutcome.Confirmed) return false;
            return await context.WaitForInputIdleAsync("after held gem restoration", 1_500, cancellationToken);
        }

        var loose = state.Inventory.Items.Where(x => x.Path == GemPath).ToArray();
        if (state.Cursor.Action != CursorView.CursorAction.Free || loose.Length != 1
            || !SkillGemReader.TryRead(state.Reader, loose[0].ItemEntity, out var actual)
            || actual.Canonical != expected.Canonical)
            return false;
        if (IsRestored(state, inventoryBefore, identitiesBefore)) return true;
        if (loose[0].Rect is not { } currentRect) return false;

        var currentPoint = state.Window.ToScreen(currentRect.CenterX, currentRect.CenterY);
        var pickup = await context.VerifiedClickAsync(currentPoint.X, currentPoint.Y, ClickIntent.InteractUi,
            $"pick up auto-returned '{expected.BaseName}' for exact-cell restoration",
            () => context.Snapshot().Cursor.Action == CursorView.CursorAction.HoldItem
                && !context.Snapshot().Inventory.Items.Any(x => x.Path == GemPath),
            3_000, cancellationToken);
        if (pickup != ActionOutcome.Confirmed
            || !await context.WaitForInputIdleAsync("after auto-returned gem pickup", 1_500, cancellationToken))
            return false;
        var place = await context.VerifiedClickAsync(originalPoint.X, originalPoint.Y, ClickIntent.InteractUi,
            $"restore auto-returned '{expected.BaseName}' to exact original cell",
            () => IsRestored(context.Snapshot(), inventoryBefore, identitiesBefore),
            3_000, cancellationToken);
        return place == ActionOutcome.Confirmed
            && await context.WaitForInputIdleAsync("after auto-returned gem restoration", 1_500, cancellationToken);
    }

    private static (InventoryView.Item Item, ItemSocketsReader.Snapshot Sockets, ItemIdentityFingerprint Identity)?
        FindSword(GameSnapshot snapshot, bool socketed)
    {
        var matches = new List<(InventoryView.Item, ItemSocketsReader.Snapshot, ItemIdentityFingerprint)>();
        foreach (var item in snapshot.Inventory.Items.Where(x => x.Path == SwordPath))
        {
            if (!ItemSocketsReader.TryRead(snapshot.Reader, item.ItemEntity, out var sockets)
                || !sockets.Colors.SequenceEqual([
                    ItemSocketsReader.SocketColor.Red,
                    ItemSocketsReader.SocketColor.Green,
                    ItemSocketsReader.SocketColor.Blue])
                || !sockets.LinkGroupSizes.SequenceEqual([1, 1, 1]))
                continue;
            var hasTarget = sockets.SocketedGems.Count == 1
                && sockets.SocketedGems[0].SocketIndex == 1
                && sockets.SocketedGems[0].MetadataPath == GemPath;
            if ((socketed && hasTarget) || (!socketed && sockets.SocketedGems.Count == 0))
                matches.Add((item, sockets, ItemIdentityFingerprint.Capture(snapshot.Reader, item)));
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool GemStateEquals(ItemSocketsReader.SocketedGem actual, SkillGemReader.Snapshot expected)
        => actual.MetadataPath == expected.MetadataPath
            && actual.BaseName == expected.BaseName
            && actual.Level == expected.Level
            && actual.TotalExperience == expected.TotalExperience
            && actual.PreviousLevelExperience == expected.PreviousLevelExperience
            && actual.NextLevelExperience == expected.NextLevelExperience;

    private static async Task<string> HoverAndReadTooltipAsync(
        LiveTestContext context, InventoryView.Item item, string label, CancellationToken cancellationToken)
    {
        if (item.Rect is not { } rect) return string.Empty;
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 150, cancellationToken);
        var reached = await context.WaitUntilAsync(label + " hover",
            () => ResolvesHoverTo(context.Snapshot(), item.ElementAddress), 1_500, cancellationToken, 20);
        var snapshot = context.Snapshot();
        snapshot.Reader.TryReadStruct<nint>(item.ElementAddress + KnownOffsets.Element.RenderedTooltip, out var tooltip);
        var fingerprint = ReadTooltip(snapshot.Reader, tooltip);
        context.Check(reached, label + " exact UIHover", $"element=0x{(long)item.ElementAddress:X}");
        context.Check(tooltip != 0 && fingerprint.Length > 0, label + " rendered tooltip",
            $"root=0x{(long)tooltip:X} text=[{fingerprint}]");
        return reached ? fingerprint : string.Empty;
    }

    private static string ReadTooltip(MemoryReader reader, nint root)
    {
        if (root == 0) return string.Empty;
        var lines = new List<string>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 1_024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            if (depth >= 14) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Join(" || ", lines);
    }

    private static bool ResolvesHoverTo(GameSnapshot snapshot, nint target)
    {
        snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var current);
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current) break;
            current = parent;
        }
        return false;
    }

    private static string PositionalFingerprint(InventoryView inventory)
        => PositionalFingerprint(inventory.Items);

    private static string PositionalFingerprint(IEnumerable<InventoryView.Item> items)
        => string.Join('|', items.OrderBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{Format(x.Rect)}"));

    private static string IdentityMultiset(GameSnapshot snapshot, params nint[] excludedElements)
    {
        var excluded = excludedElements.ToHashSet();
        return string.Join("||", snapshot.Inventory.Items.Where(x => !excluded.Contains(x.ElementAddress))
            .Select(item => ItemIdentityFingerprint.Capture(snapshot.Reader, item).Canonical)
            .OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string Format(ElementGeometry.Rect? rect)
        => rect is { } value ? $"{value.X:F2},{value.Y:F2},{value.Width:F2},{value.Height:F2}" : "none";

    private static string Symbol(ItemSocketsReader.SocketColor color) => color switch
    {
        ItemSocketsReader.SocketColor.Red => "R",
        ItemSocketsReader.SocketColor.Green => "G",
        ItemSocketsReader.SocketColor.Blue => "B",
        ItemSocketsReader.SocketColor.White => "W",
        ItemSocketsReader.SocketColor.Abyss => "A",
        ItemSocketsReader.SocketColor.Delve => "D",
        _ => "?",
    };

    private static async Task MoveNeutralAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var window = context.Snapshot().Window;
        await context.HoverAsync(window.OriginX + window.Width / 2, window.OriginY + 80, 150, cancellationToken);
    }
}
