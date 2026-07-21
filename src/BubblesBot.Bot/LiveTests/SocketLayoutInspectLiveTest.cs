using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only candidate discovery for current-build Sockets component fields.</summary>
public sealed class SocketLayoutInspectLiveTest : ILiveTestCase
{
    public string Id => "I-06-socket-layout-inspect";
    public string Name => "Socket component layout inspection";
    public string Description => "Scans visible inventory items for plausible color arrays, socketed-gem pointer arrays, and link-group byte vectors; sends no input.";
    public string ManualSetup => "Open inventory with at least two items whose visible socket colors/links differ. Socketed gems are helpful but optional.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.Snapshot();
        var socketItems = snapshot.Inventory.Items.Select(item =>
        {
            var map = EntityComponents.ReadComponentMap(snapshot.Reader, item.ItemEntity);
            return (Item: item, Component: map.TryGetValue("Sockets", out var address) ? address : 0);
        }).Where(x => x.Component != 0).ToArray();
        context.Check(snapshot.Inventory.IsOpen, "inventory open", $"open={snapshot.Inventory.IsOpen}");
        context.Check(socketItems.Length >= 2, "socket-bearing item sample", $"count={socketItems.Length}");

        foreach (var (item, component) in socketItems)
        {
            var baseName = ReadBaseName(snapshot.Reader, item.ItemEntity);
            context.Observe("socket item", $"base='{baseName}' entity=0x{(long)item.ItemEntity:X} component=0x{(long)component:X} rect={item.Rect}");
            var raw = new byte[0xC0];
            if (snapshot.Reader.TryReadBytes(component, raw) == raw.Length)
                for (var offset = 0; offset < raw.Length; offset += 16)
                    context.Observe("socket raw",
                        $"base='{baseName}' offset=+0x{offset:X2} bytes={Convert.ToHexString(raw.AsSpan(offset, 16))}");

            for (var offset = 0; offset <= 0x80; offset += 4)
            {
                var values = new int[6];
                var readable = true;
                for (var i = 0; i < values.Length; i++)
                    readable &= snapshot.Reader.TryReadStruct<int>(component + offset + i * 4, out values[i]);
                if (readable && values.All(x => x is >= 0 and <= 6) && values.Any(x => x > 0))
                    context.Observe("color-array candidate", $"base='{baseName}' offset=+0x{offset:X} values=[{string.Join(',', values)}]");
            }

            for (var offset = 0; offset <= 0x90; offset += 8)
            {
                var values = new nint[6];
                var readable = true;
                for (var i = 0; i < values.Length; i++)
                    readable &= snapshot.Reader.TryReadStruct<nint>(component + offset + i * 8, out values[i]);
                if (readable && values.Any(LooksLikeUserAddress) && values.All(x => x == 0 || LooksLikeUserAddress(x)))
                    context.Observe("pointer-array candidate", $"base='{baseName}' offset=+0x{offset:X} values=[{string.Join(',', values.Select(x => $"0x{(long)x:X}"))}]");
            }

            for (var offset = 0; offset <= 0x100; offset += 8)
            {
                if (!snapshot.Reader.TryReadStruct<StdVector>(component + offset, out var vector)
                    || !LooksLikeUserAddress(vector.First) || vector.Last < vector.First)
                    continue;
                var bytes = vector.ByteCount;
                if (bytes is < 1 or > 6) continue;
                var values = new byte[(int)bytes];
                if (snapshot.Reader.TryReadBytes(vector.First, values) == values.Length
                    && values.All(x => x is >= 1 and <= 6))
                    context.Observe("link-vector candidate", $"base='{baseName}' offset=+0x{offset:X} first=0x{(long)vector.First:X} values=[{string.Join(',', values.ToArray())}]");
            }

            for (var socketIndex = 0; socketIndex < 6; socketIndex++)
            {
                if (!snapshot.Reader.TryReadStruct<nint>(component + 0x48 + socketIndex * 8, out var gem)
                    || !LooksLikeUserAddress(gem)) continue;
                var path = EntityListReader.ReadEntityPath(snapshot.Reader, gem) ?? string.Empty;
                if (!path.StartsWith("Metadata/Items/Gems/", StringComparison.Ordinal)) continue;
                var gemComponents = EntityComponents.ReadComponentMap(snapshot.Reader, gem);
                gemComponents.TryGetValue("SkillGem", out var skillGem);
                var skillRaw = new byte[0x40];
                var rawText = skillGem != 0 && snapshot.Reader.TryReadBytes(skillGem, skillRaw) == skillRaw.Length
                    ? Convert.ToHexString(skillRaw)
                    : "unreadable";
                snapshot.Reader.TryReadStruct<uint>(skillGem + KnownOffsets.SkillGemComponent.TotalExpGained, out var exp);
                snapshot.Reader.TryReadStruct<uint>(skillGem + KnownOffsets.SkillGemComponent.Level, out var level);
                snapshot.Reader.TryReadStruct<uint>(skillGem + KnownOffsets.SkillGemComponent.ExperienceMaxLevel, out var maxExp);
                context.Observe("socketed gem candidate",
                    $"base='{baseName}' socket={socketIndex} entity=0x{(long)gem:X} path='{path}' skillGem=0x{(long)skillGem:X} knownLevel={level} knownExp={exp} knownMaxExp={maxExp} raw={rawText}");
            }
        }

        return Task.FromResult(snapshot.Inventory.IsOpen && socketItems.Length >= 2
            ? LiveTestCaseResult.Pass("socket-layout candidates recorded", "ReadOnlyDiscovery")
            : LiveTestCaseResult.Blocked("requires open inventory with two socket-bearing items", "PreparedStateMismatch"));
    }

    private static string ReadBaseName(MemoryReader reader, nint entity)
    {
        var components = EntityComponents.ReadComponentMap(reader, entity);
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0) return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;
}
