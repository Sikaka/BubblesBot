using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Discovers ServerInventory and InventoryHolder field offsets by asking POEMCP for ground
/// truth across multiple holder/inventory pairs (different InventType, Columns, Rows,
/// ItemCount), then finding offsets where every inventory's memory matches its truth value.
///
/// Output is informational — running the test once writes the discovered offsets to the
/// console; we then bake them into KnownOffsets.cs.
/// </summary>
public sealed class ServerInventoryLayoutDiscoveryTest : ValidationTest
{
    public override string Name => "ServerInventory + InventoryHolder layout discovery";
    public override string? Group => "Inventory/stash";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        var truth = await ctx.Poemcp.EvalAsync(
            """
            var sb = new System.Text.StringBuilder();
            int taken = 0;
            foreach (var ih in IngameState.Data.ServerData.PlayerInventories)
            {
                var sv = ih.Inventory;
                if (sv == null) continue;
                sb.Append(ih.Address.ToString("X")).Append(',')
                  .Append(ih.Id).Append(',')
                  .Append(sv.Address.ToString("X")).Append(',')
                  .Append((int)sv.InventType).Append(',')
                  .Append((int)sv.InventSlot).Append(',')
                  .Append(sv.Columns).Append(',')
                  .Append(sv.Rows).Append(',')
                  .Append(sv.ItemCount).Append(';');
                if (++taken >= 6) break;
            }
            sb.ToString()
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var rows = truth.AsString().Split(';', StringSplitOptions.RemoveEmptyEntries);
        var holders = rows.Select(r =>
        {
            var f = r.Split(',');
            return new HolderTruth(
                Holder:    (nint)long.Parse(f[0], System.Globalization.NumberStyles.HexNumber),
                HolderId:  int.Parse(f[1]),
                Inv:       (nint)long.Parse(f[2], System.Globalization.NumberStyles.HexNumber),
                InvType:   int.Parse(f[3]),
                InvSlot:   int.Parse(f[4]),
                Columns:   int.Parse(f[5]),
                Rows:      int.Parse(f[6]),
                ItemCount: long.Parse(f[7]));
        }).ToArray();

        if (holders.Length < 3)
            return new TestOutcome.Skip(Name, $"only {holders.Length} holders available");

        var msgs = new List<string>();

        // Holder offsets (small struct, scan 0..0x18)
        msgs.Add("InventoryHolder.Id    : " +
            string.Join(",", FindAgreementInt(ctx.Reader, holders.Select(h => (h.Holder, (int)h.HolderId)).ToArray(), 0, 0x20)));
        msgs.Add("InventoryHolder.Inv   : " +
            string.Join(",", FindAgreementPtr(ctx.Reader, holders.Select(h => (h.Holder, h.Inv)).ToArray(), 0, 0x20)));

        // ServerInventory offsets (scan 0..0x200)
        msgs.Add("ServerInventory.InventType  : " +
            string.Join(",", FindAgreementInt(ctx.Reader, holders.Select(h => (h.Inv, h.InvType)).ToArray(), 0, 0x200, requireDistinct: true)));
        msgs.Add("ServerInventory.InventSlot  : " +
            string.Join(",", FindAgreementInt(ctx.Reader, holders.Select(h => (h.Inv, h.InvSlot)).ToArray(), 0, 0x200, requireDistinct: true)));
        msgs.Add("ServerInventory.Columns     : " +
            string.Join(",", FindAgreementInt(ctx.Reader, holders.Select(h => (h.Inv, h.Columns)).ToArray(), 0, 0x200, requireDistinct: true)));
        msgs.Add("ServerInventory.Rows        : " +
            string.Join(",", FindAgreementInt(ctx.Reader, holders.Select(h => (h.Inv, h.Rows)).ToArray(), 0, 0x200, requireDistinct: true)));
        msgs.Add("ServerInventory.ItemCount   : " +
            string.Join(",", FindAgreementLong(ctx.Reader, holders.Select(h => (h.Inv, h.ItemCount)).ToArray(), 0, 0x200, requireDistinct: true)));

        // Items array — find a NativePtrArray (3 pointers) whose Count matches ItemCount.
        msgs.Add("ServerInventory.Items array : " +
            string.Join(",", FindAgreementPtrArrayCount(ctx.Reader, holders, 0, 0x300)));

        return new TestOutcome.Pass(Name, "discovery: " + string.Join(" | ", msgs));
    }

    private sealed record HolderTruth(nint Holder, int HolderId, nint Inv, int InvType, int InvSlot, int Columns, int Rows, long ItemCount);

    private static IReadOnlyList<string> FindAgreementInt(MemoryReader reader, (nint Addr, int Truth)[] samples, int start, int end, bool requireDistinct = false)
    {
        var hits = new List<int>();
        // Distinct = at least 3 distinct truth values, otherwise any match is suspect
        if (requireDistinct && samples.Select(s => s.Truth).Distinct().Count() < 2) return new[] { "(uniform truths, not discriminative)" };
        for (var off = start; off <= end; off += 4)
        {
            var ok = true;
            foreach (var (addr, truth) in samples)
            {
                if (!reader.TryReadStruct<int>(addr + off, out var v) || v != truth) { ok = false; break; }
            }
            if (ok) hits.Add(off);
        }
        return hits.Count == 0 ? new[] { "(none)" } : hits.Select(h => $"+0x{h:X}").ToArray();
    }

    private static IReadOnlyList<string> FindAgreementLong(MemoryReader reader, (nint Addr, long Truth)[] samples, int start, int end, bool requireDistinct = false)
    {
        var hits = new List<int>();
        if (requireDistinct && samples.Select(s => s.Truth).Distinct().Count() < 2) return new[] { "(uniform truths, not discriminative)" };
        for (var off = start; off <= end; off += 8)
        {
            var ok = true;
            foreach (var (addr, truth) in samples)
            {
                if (!reader.TryReadStruct<long>(addr + off, out var v) || v != truth) { ok = false; break; }
            }
            if (ok) hits.Add(off);
        }
        return hits.Count == 0 ? new[] { "(none)" } : hits.Select(h => $"+0x{h:X}").ToArray();
    }

    /// <summary>Find offsets where a NativePtrArray (3 pointers) has element count == ItemCount across all samples.</summary>
    private static IReadOnlyList<string> FindAgreementPtrArrayCount(MemoryReader reader, HolderTruth[] samples, int start, int end)
    {
        if (samples.Select(s => s.ItemCount).Distinct().Count() < 2) return new[] { "(uniform truths, not discriminative)" };
        var hits = new List<int>();
        for (var off = start; off <= end; off += 8)
        {
            var ok = true;
            foreach (var h in samples)
            {
                if (!reader.TryReadStruct<NativePtrArray>(h.Inv + off, out var arr)) { ok = false; break; }
                if (!LooksLikeUserAddress(arr.First) && h.ItemCount != 0) { ok = false; break; }
                if (h.ItemCount == 0)
                {
                    // empty array — accept if Count is 0 or pointer is null
                    if (arr.Count != 0 && arr.First != 0) { ok = false; break; }
                    continue;
                }
                if (arr.Count != h.ItemCount) { ok = false; break; }
            }
            if (ok) hits.Add(off);
        }
        return hits.Count == 0 ? new[] { "(none)" } : hits.Select(h => $"+0x{h:X}").ToArray();
    }

    private static bool LooksLikeUserAddress(nint p) => (long)p > 0x10000 && (long)p < 0x7FFF_FFFF_FFFF;

    private static IReadOnlyList<string> FindAgreementPtr(MemoryReader reader, (nint Addr, nint Truth)[] samples, int start, int end)
    {
        var hits = new List<int>();
        for (var off = start; off <= end; off += 8)
        {
            var ok = true;
            foreach (var (addr, truth) in samples)
            {
                if (!reader.TryReadStruct<nint>(addr + off, out var v) || v != truth) { ok = false; break; }
            }
            if (ok) hits.Add(off);
        }
        return hits.Count == 0 ? new[] { "(none)" } : hits.Select(h => $"+0x{h:X}").ToArray();
    }
}
