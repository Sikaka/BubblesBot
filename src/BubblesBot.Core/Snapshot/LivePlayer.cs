using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Direct, eager read of the player's hot fields — grid position, life, render world pos.
/// Use this when you want a *fresh* read every frame (e.g. the render path that needs to
/// project entity grid coordinates through the live player position at 144 Hz, while the
/// rest of the world snapshot only refreshes at 30 Hz).
///
/// <para>Cheap: 3-5 memory reads. Returns null if the player isn't resolvable (loading
/// screen, area transition).</para>
///
/// <para>This intentionally bypasses <see cref="GameSnapshot"/>. The snapshot caches reads
/// for its lifetime, which is great for tick-level work but defeats per-render freshness.
/// LivePlayer reads memory each call.</para>
/// </summary>
public readonly record struct LivePlayer(
    Vector2i GridPosition,
    Vector3  WorldPosition,
    int      HpCurrent,
    int      HpMax,
    int      ManaCurrent,
    int      ManaMax)
{
    public static LivePlayer? TryRead(MemoryReader reader, nint ingameDataAddress)
    {
        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.LocalPlayer, out var playerAddr)
            || playerAddr == 0)
            return null;

        // Component map costs ~50 µs but the addresses are stable for the entity's lifetime —
        // this is the kind of thing the V2 entity-cache will memoize. For now, eat the cost
        // per render; total budget is fine.
        var components = EntityComponents.ReadComponentMap(reader, playerAddr);

        Vector2i grid = default;
        if (components.TryGetValue("Positioned", out var positioned) && positioned != 0)
            reader.TryReadStruct(positioned + KnownOffsets.PositionedComponent.GridPosition, out grid);

        Vector3 world = default;
        if (components.TryGetValue("Render", out var render) && render != 0)
            reader.TryReadStruct(render + KnownOffsets.RenderComponent.Pos, out world);

        int hpCur = 0, hpMax = 0, manaCur = 0, manaMax = 0;
        if (components.TryGetValue("Life", out var life) && life != 0)
        {
            if (reader.TryReadStruct<VitalStruct>(life + KnownOffsets.LifeComponent.Health, out var hp) && hp.LooksValid())
            {
                hpCur = hp.Current;
                hpMax = hp.Max;
            }
            if (reader.TryReadStruct<VitalStruct>(life + KnownOffsets.LifeComponent.Mana, out var mp) && mp.LooksValid())
            {
                manaCur = mp.Current;
                manaMax = mp.Max;
            }
        }

        return new LivePlayer(grid, world, hpCur, hpMax, manaCur, manaMax);
    }
}
