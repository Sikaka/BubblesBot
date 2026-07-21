using System.Buffers;

namespace BubblesBot.Core.Game;

/// <summary>
/// Search-and-validate utility: given the player's known HP / ES / Mana values, find every memory
/// address in the target process whose surrounding bytes are consistent with a <see cref="VitalStruct"/>
/// and a parent <see cref="KnownOffsets.LifeComponent"/>. The first candidate where all three match
/// is, with overwhelming probability, the player's actual Life component.
///
/// This is what we use to validate the offset table from <c>community-offsets.md</c> WITHOUT yet
/// having a static anchor / AOB scan into IngameState. Once we trust LifeComponent's layout,
/// we can walk *back* from there to anchor everything else.
/// </summary>
public static class LifeValidator
{
    public sealed record Match(
        nint LifeComponentAddress,
        VitalStruct Health,
        VitalStruct Mana,
        VitalStruct EnergyShield,
        nint OwnerAddress)
    {
        public override string ToString() =>
            $"LifeComponent @ 0x{LifeComponentAddress:X16}\n" +
            $"  Owner          : 0x{OwnerAddress:X16}\n" +
            $"  Health         : {Health.Current,7} / {Health.Max,-7} (reservedFlat={Health.ReservedFlat}, regen={Health.Regen:F2})\n" +
            $"  Mana           : {Mana.Current,7} / {Mana.Max,-7} (reservedFlat={Mana.ReservedFlat}, regen={Mana.Regen:F2})\n" +
            $"  EnergyShield   : {EnergyShield.Current,7} / {EnergyShield.Max,-7} (reservedFlat={EnergyShield.ReservedFlat}, regen={EnergyShield.Regen:F2})";
    }

    public sealed record SearchProgress(int RegionsScanned, int TotalRegions, long BytesScanned, int CandidatesFound);

    /// <summary>
    /// Find LifeComponent candidates in the target process.
    /// </summary>
    /// <param name="reader">Memory reader for an attached PoE process.</param>
    /// <param name="expectedHpCurrent">Player's current HP. The needle for the value-scan.</param>
    /// <param name="expectedManaCurrent">Optional secondary check. If provided, candidates whose Mana.Current doesn't match are rejected.</param>
    /// <param name="expectedEsMax">Optional secondary check. If provided, candidates whose EnergyShield.Max doesn't match are rejected.</param>
    /// <param name="onProgress">Called periodically with the current scan progress (per-region).</param>
    public static IReadOnlyList<Match> FindCandidates(
        MemoryReader reader,
        int expectedHpCurrent,
        int? expectedManaCurrent = null,
        int? expectedEsMax = null,
        Action<SearchProgress>? onProgress = null)
    {
        var process = reader.Process;
        var matches = new List<Match>();

        // Snapshot the region list once â€” enumerator allocates per-call, and we want determinism.
        var regions = process.EnumerateReadableRegions(privateOnly: true).ToArray();

        long bytesScanned = 0;
        const int chunkSize = 1 * 1024 * 1024; // 1 MiB at a time â€” keeps per-read latency bounded
        var pool = ArrayPool<byte>.Shared;
        var buf = pool.Rent(chunkSize);

        try
        {
            for (var ri = 0; ri < regions.Length; ri++)
            {
                var (regionBase, regionSize) = regions[ri];
                long offset = 0;
                while (offset < regionSize)
                {
                    var toRead = (int)Math.Min(chunkSize, regionSize - offset);
                    var bufSpan = buf.AsSpan(0, toRead);
                    var read = reader.TryReadBytes(regionBase + (nint)offset, bufSpan);
                    if (read == 0)
                    {
                        // Region went away mid-scan (unmapped, free'd, page mid-region became unreadable).
                        // Skip rest of region; this is normal during a scan of a live process.
                        break;
                    }
                    if (read != toRead)
                    {
                        // Partial read â€” scan what we got and stop on this region.
                        bufSpan = bufSpan[..read];
                    }
                    bytesScanned += read;

                    // Scan for int32 = expectedHpCurrent at every 4-byte aligned address.
                    // VitalStruct fields are int32-aligned in practice; a misaligned hit would be a coincidence.
                    for (var i = 0; i + 4 <= bufSpan.Length; i += 4)
                    {
                        var v = BitConverter.ToInt32(bufSpan[i..(i + 4)]);
                        if (v != expectedHpCurrent) continue;

                        // Candidate Current is at (regionBase + offset + i). VitalStruct.Current is at +0x30.
                        // So candidate VitalStruct base is (candidate Current) - 0x30.
                        var vitalCurrentAddr = regionBase + (nint)(offset + i);
                        var vitalBase = vitalCurrentAddr - KnownOffsets.Vital.Current;
                        TryValidateCandidate(reader, vitalBase, expectedHpCurrent, expectedManaCurrent, expectedEsMax, matches);
                    }

                    if (read != toRead) break; // partial read terminates this region
                    offset += toRead;
                }

                onProgress?.Invoke(new SearchProgress(ri + 1, regions.Length, bytesScanned, matches.Count));
            }
        }
        finally
        {
            pool.Return(buf);
        }

        return matches;
    }

    private static void TryValidateCandidate(
        MemoryReader reader,
        nint candidateHealthAddr,
        int expectedHp,
        int? expectedManaCurrent,
        int? expectedEsMax,
        List<Match> sink)
    {
        // candidateHealthAddr is the hypothetical VitalStruct base for Health.
        // LifeComponent address = healthAddr - 0x178 (LifeComponent.Health offset).
        var lifeComponentAddr = candidateHealthAddr - KnownOffsets.LifeComponent.Health;

        if (!reader.TryReadStruct<VitalStruct>(candidateHealthAddr, out var health)) return;
        if (!health.LooksValid()) return;
        if (health.Current != expectedHp) return; // re-check after struct read

        if (!reader.TryReadStruct<VitalStruct>(lifeComponentAddr + KnownOffsets.LifeComponent.Mana, out var mana)) return;
        if (!mana.LooksValid()) return;

        if (!reader.TryReadStruct<VitalStruct>(lifeComponentAddr + KnownOffsets.LifeComponent.EnergyShield, out var es)) return;
        // ES can legitimately have Max=0 (no ES gear), so don't filter by LooksLikeVital here â€” accept any plausible struct.
        // But ES.Current must be in [0, ES.Max].
        if (es.Max < 0 || es.Current < 0 || es.Current > es.Max + 1) return;

        if (expectedManaCurrent.HasValue && mana.Current != expectedManaCurrent.Value) return;
        if (expectedEsMax.HasValue && es.Max != expectedEsMax.Value) return;

        // Read Owner pointer â€” should be a valid-looking pointer (high bits sane).
        var ownerAddr = reader.TryReadStruct<nint>(lifeComponentAddr + KnownOffsets.LifeComponent.Owner, out var o)
            ? o
            : 0;

        sink.Add(new Match(lifeComponentAddr, health, mana, es, ownerAddr));
    }
}
