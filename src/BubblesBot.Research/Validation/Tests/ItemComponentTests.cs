using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Validates Base component on an entity. Searches the entity list for the first entity
/// that has a Base component (ground items, items from monsters, etc.). Skips if none in area.
/// </summary>
public sealed class BaseComponentOnEntityTest : ValidationTest
{
    public override string Name => "Base component â€” Corrupted/Influence on an entity";
    public override string? Group => "Item components";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        var r = await ctx.Poemcp.EvalAsync(
            "EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.HasComponent<Base>())?.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Skip(Name, "POEMCP unreachable");

        nint entityAddr;
        try { entityAddr = r.AsAddress(); }
        catch (InvalidOperationException) { return new TestOutcome.Skip(Name, "POEMCP returned null â€” no entities with Base in this area"); }
        if (entityAddr == 0 || (long)entityAddr < 0x10000)
            return new TestOutcome.Skip(Name, "no entity with Base component in current area");

        var map = EntityComponents.ReadComponentMap(ctx.Reader, entityAddr);
        if (!map.TryGetValue("Base", out var baseAddr))
            return new TestOutcome.Skip(Name, "entity has no Base component");

        if (!ctx.Reader.TryReadStruct<BaseComponent>(baseAddr, out var bc))
            return new TestOutcome.Fail(Name, "could not read BaseComponent");

        if (bc.Corrupted > 1) return new TestOutcome.Fail(Name, $"Corrupted={bc.Corrupted} not 0/1");
        if (bc.Influence > 7) return new TestOutcome.Fail(Name, $"Influence={bc.Influence} > 7");

        var truth = await ctx.Poemcp.EvalAsync(
            $"var e = EntityListWrapper.OnlyValidEntities.FirstOrDefault(x => x.Address == 0x{entityAddr:X}); e?.GetComponent<Base>().Corrupted + \",\" + e?.GetComponent<Base>().Influence", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: corrupted={bc.Corrupted} influence={bc.Influence}");

        var parts = truth.AsString().Split(',');
        if (parts.Length >= 2 && byte.TryParse(parts[0], out var tc) && byte.TryParse(parts[1], out var ti))
        {
            if (bc.Corrupted != tc) return new TestOutcome.Fail(Name, $"Corrupted ours {bc.Corrupted} â‰  truth {tc}");
            if (bc.Influence != ti) return new TestOutcome.Fail(Name, $"Influence ours {bc.Influence} â‰  truth {ti}");
        }

        return new TestOutcome.Pass(Name, $"matches POEMCP on entity 0x{entityAddr:X}: corrupted={bc.Corrupted} influence={bc.Influence}");
    }
}

/// <summary>
/// Validates Sockets component â€” reads Sockets vector and LinkSizes on an item entity.
/// </summary>
public sealed class SocketsComponentTest : ValidationTest
{
    public override string Name => "Sockets component â€” Sockets/LinkSizes on item";
    public override string? Group => "Item components";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        // Find an item with sockets via POEMCP.
        var r = await ctx.Poemcp.EvalAsync(
            "EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.HasComponent<Sockets>() && e.Address != Player.Address)?.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Skip(Name, "POEMCP unreachable");

        nint itemAddr;
        try { itemAddr = r.AsAddress(); }
        catch (InvalidOperationException) { return new TestOutcome.Skip(Name, "POEMCP returned null â€” no socketed items nearby"); }
        if (itemAddr == 0 || (long)itemAddr < 0x10000)
            return new TestOutcome.Skip(Name, "no socketed item found nearby");

        var map = EntityComponents.ReadComponentMap(ctx.Reader, itemAddr);
        if (!map.TryGetValue("Sockets", out var socksAddr))
            return new TestOutcome.Skip(Name, "entity has no Sockets component (race?)");

        if (!ctx.Reader.TryReadStruct<SocketsComponent>(socksAddr, out var sc))
            return new TestOutcome.Fail(Name, "could not read SocketsComponent");

        // Sanity: Socket and LinkSize counts should be reasonable.
        if (!ItemSocketsReader.TryReadComponent(ctx.Reader, socksAddr, out var sockets))
            return new TestOutcome.Fail(Name, "strict socket reader rejected the component");
        return new TestOutcome.Pass(Name,
            $"sanity OK: {sockets.SocketCount} sockets, groups=[{string.Join(',', sockets.Groups)}]");
    }
}

/// <summary>
/// Validates Chest component â€” IsOpened, IsLocked, IsStrongbox fields.
/// Finds a chest/strongbox entity and reads its state.
/// </summary>
public sealed class ChestComponentTest : ValidationTest
{
    public override string Name => "Chest component â€” IsOpened/IsLocked/IsStrongbox";
    public override string? Group => "Item components";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        var r = await ctx.Poemcp.EvalAsync(
            "EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.HasComponent<Chest>())?.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Skip(Name, "POEMCP unreachable");

        nint chestAddr;
        try { chestAddr = r.AsAddress(); }
        catch (InvalidOperationException) { return new TestOutcome.Skip(Name, "POEMCP returned null â€” no chests in this area"); }
        if (chestAddr == 0 || (long)chestAddr < 0x10000)
            return new TestOutcome.Skip(Name, "no chest entity in current area");

        var map = EntityComponents.ReadComponentMap(ctx.Reader, chestAddr);
        if (!map.TryGetValue("Chest", out var chestCompAddr))
            return new TestOutcome.Skip(Name, "chest entity has no Chest component (race?)");

        if (!ctx.Reader.TryReadStruct<ChestComponent>(chestCompAddr, out var cc))
            return new TestOutcome.Fail(Name, "could not read ChestComponent");

        // Sanity: IsOpened/IsLocked/IsStrongbox should be 0 or 1.
        if (cc.IsOpened > 1 || cc.IsLocked > 1 || cc.IsStrongbox > 1)
            return new TestOutcome.Fail(Name, $"flags out of 0/1 range: opened={cc.IsOpened} locked={cc.IsLocked} strongbox={cc.IsStrongbox}");

        return new TestOutcome.Pass(Name, $"sanity OK: opened={cc.IsOpened} locked={cc.IsLocked} strongbox={cc.IsStrongbox}");
    }
}
