namespace BubblesBot.Core.Game;

/// <summary>
/// Traverses PoE's EntityList tree and builds small, read-only entity snapshots.
/// This is intentionally conservative: every traversal has bounds and pointer
/// sanity checks because entity memory churns during area transitions.
/// </summary>
public static class EntityListReader
{
    public enum EntityKind
    {
        Unknown,
        Monster,
        Chest,
        WorldItem,
        AreaTransition,
        Portal,
        TownPortal,
        Stash,
        Shrine,
        IngameIcon,
        Effect,
        Player,
    }

    public enum EntityRarity
    {
        Normal = 0,
        Magic = 1,
        Rare = 2,
        Unique = 3,
        Error = 4,
    }

    public sealed record TraversalResult(
        IReadOnlyList<nint> EntityAddresses,
        int NodesVisited,
        int BadReads,
        bool HitSafetyLimit);

    public sealed record PathfindingSnapshot(
        bool IsMoving,
        Vector2i WantMoveToPosition,
        float StayTime,
        int DestinationNodes,
        IReadOnlyList<Vector2i> PathingNodes);

    public sealed record StateMachineSnapshot(
        bool CanBeTarget,
        bool InTarget);

    public sealed record EntitySnapshot(
        nint Address,
        uint Id,
        string Path,
        string Metadata,
        EntityKind Kind,
        IReadOnlyDictionary<string, nint> Components,
        Vector2i? GridPosition,
        VitalStruct? Health,
        bool? IsTargetable,
        EntityRarity? Rarity,
        PathfindingSnapshot? Pathfinding,
        StateMachineSnapshot? StateMachine)
    {
        public bool IsAlive => Health is { Current: > 0 };
        public bool IsHostile => Kind == EntityKind.Monster
            && !Metadata.Contains("/AnimatedItem/", StringComparison.OrdinalIgnoreCase);
        public bool LooksLikeMonster => Metadata.Contains("/Monsters/", StringComparison.OrdinalIgnoreCase);
    }

    public static TraversalResult EnumerateEntityAddresses(MemoryReader reader, nint entityListAddress, int maxNodes = 20_000)
    {
        var addresses = new List<nint>(512);
        var visited = new HashSet<nint>();
        var queue = new Queue<nint>();
        var badReads = 0;

        if (entityListAddress == 0)
            return new TraversalResult(addresses, 0, 0, false);

        // ExileApi starts traversal from *(EntityList + 0x8), then walks the
        // node's FirstAddr/SecondAddr child links. The root/sentinel itself is
        // not an entity node. Do not trust EntityList.IsEmpty as a hard gate:
        // current ExileAPI can expose valid OnlyValidEntities while this byte is
        // non-zero in the wrapper we read.
        if (!reader.TryReadStruct<nint>(entityListAddress + KnownOffsets.EntityList.Root, out var root) || !LooksLikeUserAddress(root))
            return new TraversalResult(addresses, 0, 1, false);

        queue.Enqueue(root);
        var nodesVisited = 0;
        var hitSafetyLimit = false;

        while (queue.Count > 0)
        {
            if (++nodesVisited > maxNodes)
            {
                hitSafetyLimit = true;
                break;
            }

            var nodeAddress = queue.Dequeue();
            if (!LooksLikeUserAddress(nodeAddress) || !visited.Add(nodeAddress))
                continue;

            if (!reader.TryReadStruct<EntityListStruct>(nodeAddress, out var node))
            {
                badReads++;
                continue;
            }

            if (nodeAddress != root && LooksLikeUserAddress(node.Entity))
                addresses.Add(node.Entity);

            if (LooksLikeUserAddress(node.FirstAddr) && !visited.Contains(node.FirstAddr))
                queue.Enqueue(node.FirstAddr);
            if (LooksLikeUserAddress(node.SecondAddr) && !visited.Contains(node.SecondAddr))
                queue.Enqueue(node.SecondAddr);
        }

        return new TraversalResult(addresses.Distinct().ToArray(), nodesVisited, badReads, hitSafetyLimit);
    }

    public static EntitySnapshot? TryReadSnapshot(MemoryReader reader, nint entityAddress)
    {
        if (!LooksLikeUserAddress(entityAddress)) return null;
        if (!reader.TryReadStruct<uint>(entityAddress + KnownOffsets.Entity.Id, out var id)) return null;
        if (id == 0) return null;

        var path = ReadPath(reader, entityAddress);
        var metadata = path;
        var split = metadata.IndexOf('@');
        if (split >= 0) metadata = metadata[..split];

        var components = EntityComponents.ReadComponentMap(reader, entityAddress);
        var kind = Classify(metadata, components);
        Vector2i? grid = null;
        VitalStruct? health = null;
        bool? isTargetable = null;
        EntityRarity? rarity = null;
        PathfindingSnapshot? pathfinding = null;
        StateMachineSnapshot? stateMachine = null;

        if (components.TryGetValue("Positioned", out var positioned)
            && reader.TryReadStruct<Vector2i>(positioned + KnownOffsets.PositionedComponent.GridPosition, out var gridValue)
            && Math.Abs(gridValue.X) < 50_000
            && Math.Abs(gridValue.Y) < 50_000)
        {
            grid = gridValue;
        }

        if (components.TryGetValue("Life", out var life)
            && reader.TryReadStruct<VitalStruct>(life + KnownOffsets.LifeComponent.Health, out var healthValue)
            && healthValue.LooksValid())
        {
            health = healthValue;
        }

        if (components.TryGetValue("Targetable", out var targetable)
            && reader.TryReadStruct<byte>(targetable + KnownOffsets.TargetableComponent.IsTargetable, out var targetableValue))
        {
            isTargetable = targetableValue != 0;
        }

        // Rarity from ObjectMagicProperties +0x7C (best guess from old ExileApi). When
        // White-only samples are available we can't disambiguate the offset. If magic/rare
        // monsters appear and dots stay white, re-scan via the Research harness.
        if (components.TryGetValue("ObjectMagicProperties", out var ompAddr)
            && reader.TryReadStruct<int>(ompAddr + KnownOffsets.ObjectMagicPropertiesComponent.Rarity, out var rarityVal)
            && rarityVal >= 0 && rarityVal <= 4)
        {
            rarity = (EntityRarity)rarityVal;
        }

        if (components.TryGetValue("Pathfinding", out var pathfindingAddress))
        {
            pathfinding = TryReadPathfinding(reader, pathfindingAddress);
        }

        if (components.TryGetValue("StateMachine", out var stateMachineAddress)
            && reader.TryReadStruct<byte>(stateMachineAddress + KnownOffsets.StateMachineComponent.CanBeTarget, out var canBeTarget)
            && reader.TryReadStruct<byte>(stateMachineAddress + KnownOffsets.StateMachineComponent.InTarget, out var inTarget))
        {
            stateMachine = new StateMachineSnapshot(canBeTarget == 1, inTarget == 1);
        }

        return new EntitySnapshot(entityAddress, id, path, metadata, kind, components, grid, health, isTargetable, rarity, pathfinding, stateMachine);
    }

    private static PathfindingSnapshot? TryReadPathfinding(MemoryReader reader, nint pathfinding)
    {
        if (!reader.TryReadStruct<byte>(pathfinding + KnownOffsets.PathfindingComponent.IsMoving, out var isMovingRaw))
            return null;
        if (!reader.TryReadStruct<Vector2i>(pathfinding + KnownOffsets.PathfindingComponent.WantMoveToPosition, out var wanted))
            return null;
        if (!reader.TryReadStruct<float>(pathfinding + KnownOffsets.PathfindingComponent.StayTime, out var stayTime))
            return null;
        if (!reader.TryReadStruct<int>(pathfinding + KnownOffsets.PathfindingComponent.DestinationNodes, out var destinationNodes))
            return null;

        if (destinationNodes < 0 || destinationNodes > 64)
            return null;

        var nodes = new Vector2i[destinationNodes];
        for (var i = 0; i < destinationNodes; i++)
        {
            var offset = KnownOffsets.PathfindingComponent.PathingNodes + (destinationNodes - 1 - i) * 8;
            if (!reader.TryReadStruct<Vector2i>(pathfinding + offset, out var node)
                || Math.Abs(node.X) > 50_000
                || Math.Abs(node.Y) > 50_000)
            {
                return null;
            }

            nodes[i] = node;
        }

        return new PathfindingSnapshot(isMovingRaw != 0, wanted, stayTime, destinationNodes, nodes);
    }

    private static EntityKind Classify(string metadata, IReadOnlyDictionary<string, nint> components)
    {
        if (components.ContainsKey("Player")) return EntityKind.Player;
        if (components.ContainsKey("WorldItem")) return EntityKind.WorldItem;
        if (metadata.StartsWith("Metadata/Monsters/", StringComparison.OrdinalIgnoreCase)) return EntityKind.Monster;
        if (metadata.StartsWith("Metadata/Chests/", StringComparison.OrdinalIgnoreCase)) return EntityKind.Chest;
        if (metadata.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase)) return EntityKind.AreaTransition;
        if (metadata.Contains("TownPortal", StringComparison.OrdinalIgnoreCase)) return EntityKind.TownPortal;
        if (metadata.Contains("Portal", StringComparison.OrdinalIgnoreCase)) return EntityKind.Portal;
        if (metadata.Contains("MiscellaneousObjects/Stash", StringComparison.OrdinalIgnoreCase)) return EntityKind.Stash;
        if (metadata.Contains("Shrine", StringComparison.OrdinalIgnoreCase)) return EntityKind.Shrine;
        if (metadata.StartsWith("Metadata/Effects/", StringComparison.OrdinalIgnoreCase)) return EntityKind.Effect;
        return EntityKind.Unknown;
    }

    /// <summary>
    /// Read just an entity's metadata path (e.g. <c>Metadata/Items/Currency/CurrencyPortal</c>).
    /// Cheap — does not touch the component list. Returns empty string on bad reads or
    /// non-Metadata entities.
    /// </summary>
    public static string ReadEntityPath(MemoryReader reader, nint entityAddress)
        => ReadPath(reader, entityAddress);

    private static string ReadPath(MemoryReader reader, nint entityAddress)
    {
        if (!reader.TryReadStruct<nint>(entityAddress + KnownOffsets.Entity.EntityDetailsPtr, out var details)
            || !LooksLikeUserAddress(details))
            return string.Empty;

        if (!reader.TryReadStruct<NativeUtf16Text>(details + KnownOffsets.ObjectHeader.Name, out var name)
            || name.Length <= 0
            || name.Length > 512)
            return string.Empty;

        var path = name.Length <= 7
            ? reader.ReadStringUtf16(details + KnownOffsets.ObjectHeader.Name, (int)name.Length)
            : reader.ReadStringUtf16(name.Buffer, (int)name.Length);

        return path.StartsWith("Metadata", StringComparison.Ordinal) ? path : string.Empty;
    }

    private static bool LooksLikeUserAddress(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
