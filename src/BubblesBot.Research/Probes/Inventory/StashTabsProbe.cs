using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Inventory;

public sealed class StashTabsProbe : IProbe
{
    public string Name => "inventory.stash-tabs";
    public string Group => "inventory";
    public string Description => "Server stash-tab names and candidate display-index fields.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var server = ctx.Chain.ServerData;
        if (server == 0
            || !ctx.Reader.TryReadStruct<StdVector>(
                server + KnownOffsets.ServerData.PlayerStashTabs, out var tabs))
            return ProbeResult.Fail("stash-tab vector unavailable");

        var size = KnownOffsets.ServerData.StashTabElementSize;
        var count = tabs.ByteCount / size;
        if (count is < 1 or > 4096 || tabs.ByteCount % size != 0)
            return ProbeResult.Fail($"invalid vector bytes={tabs.ByteCount} size={size}");

        var rows = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var address = tabs.First + i * size;
            var strings = new List<string>();
            for (var offset = 0; offset <= size - 0x20; offset += 8)
            {
                var value = NativeString.Read(ctx.Reader, address + offset);
                if (value.Length is > 0 and <= 64 && value.All(c => !char.IsControl(c)))
                    strings.Add($"+0x{offset:X}='{value}'");
            }
            var small = new List<string>();
            for (var offset = 0x28; offset <= size - 2; offset += 2)
            {
                if (ctx.Reader.TryReadStruct<ushort>(address + offset, out var value)
                    && value is > 0 and < 256)
                    small.Add($"+0x{offset:X}={value}");
            }
            rows.Add($"[{i}] {string.Join(' ', strings)} small({string.Join(',', small)})");
        }
        return ProbeResult.Pass($"count={count}: " + string.Join(" | ", rows));
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var candidates = new List<OffsetCandidate>();
        var server = ctx.Chain.ServerData;
        if (server == 0) return ProbeResult.Found("server stash tabs", candidates);

        // Patch drift can move the vector and change the element stride independently. Search a
        // bounded window around the committed field and require several element-shaped records:
        // a printable NativeString plus small type/display-index fields. This is read-only and
        // deliberately emits candidates for human review rather than mutating offsets.
        for (var vectorOffset = Math.Max(0, KnownOffsets.ServerData.PlayerStashTabs - 0x8000);
             vectorOffset <= KnownOffsets.ServerData.PlayerStashTabs + 0x8000;
             vectorOffset += 8)
        {
            if (!ctx.Reader.TryReadStruct<StdVector>(server + vectorOffset, out var vector)
                || vector.First == 0 || vector.ByteCount <= 0 || vector.ByteCount > 0x40000)
                continue;
            for (var stride = 0x60; stride <= 0x88; stride += 8)
            {
                if (vector.ByteCount % stride != 0) continue;
                var count = vector.ByteCount / stride;
                if (count is < 1 or > 1000) continue;
                var shaped = 0;
                var samples = new List<string>();
                for (var i = 0; i < Math.Min(5, count); i++)
                {
                    var item = vector.First + i * stride;
                    var name = NativeString.Read(ctx.Reader, item + 0x08);
                    if (name.Length is < 2 or > 64
                        || !name.Any(char.IsLetter)
                        || !name.All(c => c is >= ' ' and <= '~')
                        || !ctx.Reader.TryReadStruct<uint>(item + 0x34, out var type)
                        || type > 32
                        || !ctx.Reader.TryReadStruct<ushort>(item + 0x38, out var display)
                        || display >= 2000)
                        continue;
                    shaped++;
                    samples.Add(name);
                }
                if (shaped >= Math.Min(3, count))
                    candidates.Add(new OffsetCandidate(vectorOffset,
                        $"stride=0x{stride:X} count={count} samples=[{string.Join(" | ", samples)}]"));
            }
        }
        return ProbeResult.Found("server stash tabs", candidates);
    }
}
