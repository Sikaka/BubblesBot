using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using System.Buffers.Binary;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only targeted tree dump for the persistent gem-level-up panel.</summary>
public sealed class GemLevelUiInspectLiveTest : ILiveTestCase
{
    public string Id => "U-07-gem-level-ui-inspect";
    public string Name => "Gem level-up UI structure inspection";
    public string Description => "Dumps the bounded GemLvlUpPanel subtree, including paths, geometry, visibility, flags, text, and child counts; sends no input.";
    public string ManualSetup => "Have at least two gems ready to level so their individual rows and the All control are visible. Do not click them.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        if (!snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0
            || !snapshot.Reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.GemLvlUpPanel, out var panel)
            || panel == 0)
            return Task.FromResult(LiveTestCaseResult.Fail("GemLvlUpPanel pointer was unreadable", "PanelMissing"));

        var panelRect = ElementGeometry.TryReadRect(snapshot.Reader, panel);
        var panelVisible = ElementReader.IsVisibleDeep(snapshot.Reader, panel);
        context.Check(panelVisible && panelRect is { Width: > 0, Height: > 0 }, "gem level panel visible",
            $"panel=0x{(long)panel:X} rect={panelRect}");

        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        var nodes = new List<Node>();
        queue.Enqueue((panel, "panel", 0));
        while (queue.Count > 0 && seen.Count < 512)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(snapshot.Reader, address, 128);
            if (element is null) continue;
            var rect = ElementGeometry.TryReadRect(snapshot.Reader, address);
            snapshot.Reader.TryReadStruct<uint>(address + KnownOffsets.Element.Flags, out var flags);
            var text = NativeString.Read(snapshot.Reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(snapshot.Reader, address + KnownOffsets.Element.Text);
            snapshot.Reader.TryReadStruct<nint>(address + KnownOffsets.NormalInventoryItem.Item, out var itemEntity);
            var itemPath = itemEntity == 0 ? string.Empty : EntityListReader.ReadEntityPath(snapshot.Reader, itemEntity) ?? string.Empty;
            nodes.Add(new Node(address, path, depth, rect, element.Children.Count,
                ElementReader.IsVisibleDeep(snapshot.Reader, address), flags, OneLine(text), itemEntity, itemPath));
            if (depth >= 12) continue;
            for (var index = 0; index < element.Children.Count; index++)
                queue.Enqueue((element.Children[index], $"{path}/{index}", depth + 1));
        }

        context.Check(nodes.Count >= 8, "bounded panel traversal", $"nodes={nodes.Count}");
        foreach (var node in nodes.OrderBy(x => x.Path, StringComparer.Ordinal))
            context.Observe("gem level UI node",
                $"element=0x{(long)node.Address:X} path={node.Path} depth={node.Depth} rect={node.Rect} " +
                $"visible={node.Visible} children={node.Children} flags=0x{node.Flags:X8} text='{node.Text}' " +
                $"item=0x{(long)node.ItemEntity:X} itemPath='{node.ItemPath}'");

        foreach (var row in nodes.Where(x => x.Visible && x.Children == 4 && x.Depth == 4))
        {
            var candidates = FindGemPointers(snapshot.Reader, row.Address);
            context.Observe("gem row entity-pointer scan",
                $"row=0x{(long)row.Address:X} path={row.Path} candidates=[{string.Join(", ", candidates)}]");
        }

        var equipment = EquipmentInventoriesView.From(snapshot);
        var equippedGems = new List<EquippedGem>();
        foreach (var inventory in equipment.ServerInventories.OrderBy(x => x.HolderIndex))
        {
            foreach (var item in ServerInventoryItemsReader.Read(snapshot.Reader, inventory.Address))
            {
                if (!ItemSocketsReader.TryRead(snapshot.Reader, item.EntityAddress, out var sockets)) continue;
                foreach (var gem in sockets.SocketedGems.OrderBy(x => x.SocketIndex))
                    equippedGems.Add(new EquippedGem(
                        gem.EntityAddress,
                        gem.SocketIndex,
                        inventory.HolderIndex,
                        inventory.InventoryType,
                        inventory.InventorySlot,
                        item.EntityAddress,
                        gem.Canonical));
            }
        }
        context.Observe("equipped socketed gem enumeration",
            equippedGems.Count == 0 ? "none" : string.Join(" | ", equippedGems.Select(x =>
                $"holder={x.HolderIndex} type={x.InventoryType}/{x.InventorySlot} " +
                $"item=0x{(long)x.ItemEntity:X} socket={x.SocketIndex} gemEntity=0x{(long)x.EntityAddress:X} gem=[{x.Canonical}]")));

        var referenceChains = FindGemReferenceChains(snapshot.Reader, nodes, equippedGems);
        context.Observe("gem level UI backing-reference scan",
            referenceChains.Count == 0 ? "no direct or one-indirection equipped-gem references found"
                : string.Join(" | ", referenceChains));

        var view = GemLevelUpView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        context.Check(view.IsVisible, "strict gem level-up view", $"panel=0x{(long)view.Panel:X} rows={view.Rows.Count}");
        context.Check(view.Rows.Count == 2, "strict view has two rows", $"rows={view.Rows.Count}");
        context.Check(view.AllControl is not null, "strict view has All control",
            view.AllControl is { } all ? $"element=0x{(long)all.Element:X} rect={all.Rect} flags=0x{all.Flags:X8}" : "missing");
        var equippedByEntity = equippedGems.ToDictionary(x => x.EntityAddress);
        foreach (var row in view.Rows)
        {
            var exactEquippedMatch = equippedByEntity.TryGetValue(row.GemEntity, out var equipped);
            context.Check(exactEquippedMatch, "row entity is an equipped socketed gem",
                $"row=0x{(long)row.Element:X} gem=0x{(long)row.GemEntity:X} " +
                (exactEquippedMatch ? $"socket={equipped!.SocketIndex}" : "not in equipped socket map"));
            context.Observe("strict gem level-up row",
                $"row=0x{(long)row.Element:X} gem=0x{(long)row.GemEntity:X} name='{row.Gem.BaseName}' " +
                $"metadata='{row.Gem.MetadataPath}' level={row.Gem.Level} rawXp={row.Gem.TotalExperience} " +
                $"range={row.Gem.PreviousLevelExperience}-{row.Gem.NextLevelExperience} " +
                $"levelControl=0x{(long)row.LevelControl.Element:X}@{row.LevelControl.Rect} " +
                $"dismissControl=0x{(long)row.DismissControl.Element:X}@{row.DismissControl.Rect}");
        }
        context.Check(view.Rows.Any(x => x.Gem.MetadataPath.EndsWith("SkillGemSplittingSteel", StringComparison.Ordinal)),
            "Splitting Steel row identified", string.Join(", ", view.Rows.Select(x => x.Gem.MetadataPath)));
        context.Check(view.Rows.Any(x => x.Gem.MetadataPath.EndsWith("SupportGemChanceToBleed", StringComparison.Ordinal)),
            "Chance to Bleed row identified", string.Join(", ", view.Rows.Select(x => x.Gem.MetadataPath)));

        var levelTexts = nodes.Count(x => x.Visible && x.Text.Equals("Click to level up", StringComparison.OrdinalIgnoreCase));
        var allTexts = nodes.Count(x => x.Visible && x.Text.Equals("all", StringComparison.OrdinalIgnoreCase));
        context.Check(levelTexts == 3, "two gem rows plus aggregate level text", $"Click to level up count={levelTexts}");
        context.Check(allTexts == 1, "aggregate All label", $"all count={allTexts}");
        return Task.FromResult(levelTexts == 3 && allTexts == 1 && view.Rows.Count == 2 && view.AllControl is not null
            ? LiveTestCaseResult.Pass($"captured {nodes.Count} GemLvlUpPanel nodes with two gem rows and All control", "ReadOnlyCapture")
            : LiveTestCaseResult.Fail("prepared two-gem/All shape did not match", "PanelShapeMismatch"));
    }

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', '|').Trim();

    private static IReadOnlyList<string> FindGemPointers(MemoryReader reader, nint row)
    {
        var found = new List<string>();
        for (var offset = 0; offset <= 0x800; offset += 8)
        {
            if (!reader.TryReadStruct<nint>(row + offset, out var candidate)
                || (long)candidate is <= 0x10000 or >= 0x7FFF_FFFF_FFFF)
                continue;
            var path = EntityListReader.ReadEntityPath(reader, candidate) ?? string.Empty;
            if (path.StartsWith("Metadata/Items/Gems/", StringComparison.Ordinal))
                found.Add($"+0x{offset:X}=0x{(long)candidate:X}:{path}");
        }
        return found;
    }

    /// <summary>
    /// Searches the bounded panel subtree for exact equipped-gem entity pointers, either embedded
    /// directly in an element or in one object referenced by an element. This is deliberately a
    /// research-only scan; production row identity must use a proven, named offset/path.
    /// </summary>
    private static IReadOnlyList<string> FindGemReferenceChains(
        MemoryReader reader,
        IReadOnlyList<Node> nodes,
        IReadOnlyList<EquippedGem> gems)
    {
        const int elementBytes = 0x1000;
        const int pointeeBytes = 0x600;
        const int maximumPointeesPerNode = 384;
        var targets = gems.GroupBy(x => x.EntityAddress).ToDictionary(x => (long)x.Key, x => x.First());
        if (targets.Count == 0) return [];

        var found = new HashSet<string>(StringComparer.Ordinal);
        var elementBuffer = new byte[elementBytes];
        var pointeeBuffer = new byte[pointeeBytes];
        foreach (var node in nodes)
        {
            Array.Clear(elementBuffer);
            var bytesRead = reader.TryReadBytes(node.Address, elementBuffer);
            if (bytesRead < sizeof(long)) continue;

            var pointees = new HashSet<long>();
            for (var offset = 0; offset + sizeof(long) <= bytesRead; offset += sizeof(long))
            {
                var value = BinaryPrimitives.ReadInt64LittleEndian(elementBuffer.AsSpan(offset, sizeof(long)));
                if (targets.TryGetValue(value, out var direct))
                    found.Add($"{node.Path}+0x{offset:X} -> {Describe(direct)}");
                if (LooksLikeUserAddress((nint)value) && pointees.Count < maximumPointeesPerNode)
                    pointees.Add(value);
            }

            foreach (var pointee in pointees)
            {
                Array.Clear(pointeeBuffer);
                var pointeeRead = reader.TryReadBytes((nint)pointee, pointeeBuffer);
                if (pointeeRead < sizeof(long)) continue;
                for (var inner = 0; inner + sizeof(long) <= pointeeRead; inner += sizeof(long))
                {
                    var value = BinaryPrimitives.ReadInt64LittleEndian(pointeeBuffer.AsSpan(inner, sizeof(long)));
                    if (targets.TryGetValue(value, out var gem))
                        found.Add($"{node.Path}+? -> 0x{pointee:X}+0x{inner:X} -> {Describe(gem)}");
                }
            }
        }
        return found.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static string Describe(EquippedGem gem)
        => $"gemEntity=0x{(long)gem.EntityAddress:X} socket={gem.SocketIndex} [{gem.Canonical}]";

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;

    private sealed record Node(
        nint Address,
        string Path,
        int Depth,
        ElementGeometry.Rect? Rect,
        int Children,
        bool Visible,
        uint Flags,
        string Text,
        nint ItemEntity,
        string ItemPath);

    private sealed record EquippedGem(
        nint EntityAddress,
        int SocketIndex,
        int HolderIndex,
        int InventoryType,
        int InventorySlot,
        nint ItemEntity,
        string Canonical);
}
