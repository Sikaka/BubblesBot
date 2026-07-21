using System.Buffers;

namespace BubblesBot.Core.Game;

/// <summary>
/// POEMCP-free anchor discovery: given a known address (e.g., player Entity), scan all readable
/// memory for 8-byte-aligned positions holding that exact pointer value. For each hit, the parent
/// struct is at (hit - knownFieldOffset). Additional sanity reads at sibling offsets validate the
/// candidate.
///
/// Used to find IngameData by back-walking from player Entity:
///   IngameData has LocalPlayer at +0x8E8, EntityList at +0x9A0, ServerData at +0x8E0.
///   Scan for pointer == playerEntityAddr; candidate IngameData = (hit - 0x8E8).
///   Validate: candidate+0x9A0 and candidate+0x8E0 must also be non-zero pointers into committed memory.
/// </summary>
public static class AnchorBackWalker
{
    public sealed record IngameDataHit(nint IngameDataAddress, nint LocalPlayer, nint EntityList, nint ServerData);

    public static IReadOnlyList<IngameDataHit> FindIngameDataFromPlayer(MemoryReader reader, nint playerEntityAddress)
    {
        var hits = new List<IngameDataHit>();
        var process = reader.Process;

        var pool = ArrayPool<byte>.Shared;
        const int chunk = 1 * 1024 * 1024;
        var buf = pool.Rent(chunk);
        try
        {
            foreach (var (regionBase, regionSize) in process.EnumerateReadableRegions(privateOnly: true))
            {
                long offset = 0;
                while (offset < regionSize)
                {
                    var toRead = (int)Math.Min(chunk, regionSize - offset);
                    var span = buf.AsSpan(0, toRead);
                    var read = reader.TryReadBytes(regionBase + (nint)offset, span);
                    if (read == 0) break;
                    if (read != toRead) span = span[..read];

                    // Scan for 8-byte-aligned pointer == playerEntityAddress.
                    for (var i = 0; i + 8 <= span.Length; i += 8)
                    {
                        var p = BitConverter.ToInt64(span[i..(i + 8)]);
                        if ((nint)p != playerEntityAddress) continue;

                        // Candidate IngameData base: (hit address - LocalPlayer offset 0x8E8)
                        var hitAddr = regionBase + (nint)(offset + i);
                        var candidate = hitAddr - KnownOffsets.IngameData.LocalPlayer;
                        if (TryValidateIngameData(reader, candidate, playerEntityAddress, out var hit))
                            hits.Add(hit);
                    }

                    if (read != toRead) break;
                    offset += toRead;
                }
            }
        }
        finally
        {
            pool.Return(buf);
        }

        return hits;
    }

    private static bool TryValidateIngameData(MemoryReader reader, nint candidate, nint expectedLocalPlayer, out IngameDataHit hit)
    {
        hit = null!;

        // 1. The pointer at candidate+0x8E8 must equal the known player address (matches our hit).
        if (!reader.TryReadStruct<nint>(candidate + KnownOffsets.IngameData.LocalPlayer, out var lp)) return false;
        if (lp != expectedLocalPlayer) return false;

        // 2. EntityList @ +0x9A0 must be a plausible pointer.
        if (!reader.TryReadStruct<nint>(candidate + KnownOffsets.IngameData.EntityList, out var el)) return false;
        if (!LooksLikeUserAddress(el)) return false;

        // 3. ServerData @ +0x8E0 must be a plausible pointer to readable memory.
        if (!reader.TryReadStruct<nint>(candidate + KnownOffsets.IngameData.ServerData, out var sd)) return false;
        if (!LooksLikeUserAddress(sd) || !reader.TryReadStruct<long>(sd, out _)) return false;

        // 4. EntitiesCount @ +0x9A8 must be a plausible non-negative count.
        if (!reader.TryReadStruct<int>(candidate + KnownOffsets.IngameData.EntitiesCount, out var entitiesCount)) return false;
        if (entitiesCount < 0 || entitiesCount > 100_000) return false;

        // 5. CurrentArea @ +0xA8 must be a plausible pointer.
        if (!reader.TryReadStruct<nint>(candidate + KnownOffsets.IngameData.CurrentArea, out var currentArea)) return false;
        if (!LooksLikeUserAddress(currentArea)) return false;

        // 6. CurrentAreaLevel @ +0xCC must be a plausible PoE area level.
        if (!reader.TryReadStruct<byte>(candidate + KnownOffsets.IngameData.CurrentAreaLevel, out var level)) return false;
        if (level < 1 || level > 100) return false;

        // 7. IngameStatePtr @ +0x2A8 â€” back-pointer to IngameState. The pointer at this offset
        //    should itself, when offset by IngameStateOffsets.Data (0x218), point back to candidate.
        //    This is an extremely strong filter â€” it's a closed loop only the real IngameData satisfies.
        if (!reader.TryReadStruct<nint>(candidate + KnownOffsets.IngameData.IngameStatePtr, out var ingameStatePtr)) return false;
        if (!LooksLikeUserAddress(ingameStatePtr)) return false;
        if (!reader.TryReadStruct<nint>(ingameStatePtr + KnownOffsets.IngameState.Data, out var roundtrip)) return false;
        if (roundtrip != candidate) return false;

        hit = new IngameDataHit(candidate, lp, el, sd);
        return true;
    }

    /// <summary>x64 Windows user-mode addresses live in the lower ~140TB. Reject obviously bogus values.</summary>
    private static bool LooksLikeUserAddress(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
