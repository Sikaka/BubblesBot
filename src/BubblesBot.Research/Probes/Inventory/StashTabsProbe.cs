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
        if (count is < 1 or > 1000 || tabs.ByteCount % size != 0)
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
        => ProbeResult.Found("server stash tabs", []);
}
