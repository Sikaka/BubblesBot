using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Exact read-only fixture for the two deliberately differentiated Rusted Swords.</summary>
public sealed class ItemSocketIdentityLiveTest : ILiveTestCase
{
    private const string SwordPath = "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1";
    private const string DoubleStrikePath = "Metadata/Items/Gems/SkillGemDoubleStrike";
    private const string ChanceToBleedPath = "Metadata/Items/Gems/SupportGemChanceToBleed";

    public string Id => "I-06-socket-item-identity";
    public string Name => "Socket-aware item identity fixture";
    public string Description => "Proves two otherwise identical Rusted Swords are distinguished by colors, link groups, and socketed gem identities/levels/XP.";
    public string ManualSetup => "Inventory open with Sword 1 RGB/unlinked/empty and Sword 2 GR/two-linked containing level-1 Double Strike and level-1 Chance to Bleed.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.Snapshot();
        var swords = snapshot.Inventory.Items.Where(x => x.Path == SwordPath)
            .Select(item =>
            {
                var readable = ItemSocketsReader.TryRead(snapshot.Reader, item.ItemEntity, out var sockets);
                return (Item: item, Readable: readable, Sockets: sockets,
                    Identity: ItemIdentityFingerprint.Capture(snapshot.Reader, item));
            }).ToArray();
        var ok = context.Check(snapshot.Inventory.IsOpen, "inventory open", $"open={snapshot.Inventory.IsOpen}");
        ok &= context.Check(swords.Length == 2, "two Rusted Sword fixtures", $"count={swords.Length}");
        if (swords.Length != 2)
            return Task.FromResult(LiveTestCaseResult.Blocked("exact two-sword fixture is required", "PreparedStateMismatch"));

        foreach (var sword in swords)
        {
            ok &= context.Check(sword.Readable && sword.Identity.SocketDetailsValidated,
                "strict socket read", $"rect={sword.Item.Rect} readable={sword.Readable} canonical=[{sword.Sockets?.Canonical}]");
            context.Observe("socket-aware sword identity",
                $"rect={sword.Item.Rect} sockets=[{sword.Sockets?.Canonical}] identity=[{sword.Identity.Canonical}]");
        }

        var rgb = swords.SingleOrDefault(x => x.Sockets?.Colors.SequenceEqual([
            ItemSocketsReader.SocketColor.Red,
            ItemSocketsReader.SocketColor.Green,
            ItemSocketsReader.SocketColor.Blue]) == true);
        var linked = swords.SingleOrDefault(x => x.Sockets?.Colors.SequenceEqual([
            ItemSocketsReader.SocketColor.Green,
            ItemSocketsReader.SocketColor.Red]) == true);
        ok &= context.Check(rgb.Sockets is not null, "Sword 1 RGB identity", rgb.Sockets?.Canonical ?? "missing");
        ok &= context.Check(rgb.Sockets is { LinkGroupSizes.Count: 3, SocketedGems.Count: 0 }
                && rgb.Sockets.LinkGroupSizes.SequenceEqual([1, 1, 1]),
            "Sword 1 unlinked and empty", rgb.Sockets?.Canonical ?? "missing");
        ok &= context.Check(linked.Sockets is not null, "Sword 2 GR identity", linked.Sockets?.Canonical ?? "missing");
        ok &= context.Check(linked.Sockets is { LinkGroupSizes.Count: 1, SocketedGems.Count: 2 }
                && linked.Sockets.LinkGroupSizes.SequenceEqual([2]),
            "Sword 2 two-link and two gems", linked.Sockets?.Canonical ?? "missing");

        var doubleStrike = linked.Sockets?.SocketedGems.SingleOrDefault(x => x.SocketIndex == 0);
        var chanceToBleed = linked.Sockets?.SocketedGems.SingleOrDefault(x => x.SocketIndex == 1);
        ok &= context.Check(doubleStrike is { MetadataPath: DoubleStrikePath, Level: 1 },
            "socket 0 Double Strike level", doubleStrike?.Canonical ?? "missing");
        ok &= context.Check(chanceToBleed is { MetadataPath: ChanceToBleedPath, Level: 1 },
            "socket 1 Chance to Bleed level", chanceToBleed?.Canonical ?? "missing");
        ok &= context.Check(rgb.Identity?.Canonical != linked.Identity?.Canonical,
            "otherwise identical swords have distinct canonical identities",
            $"rgb=[{rgb.Identity?.Canonical}] linked=[{linked.Identity?.Canonical}]");

        return Task.FromResult(ok
            ? LiveTestCaseResult.Pass("socket-aware canonical identities distinguish the two Rusted Swords", "ValidatedFixture")
            : LiveTestCaseResult.Fail("socket-aware identity fixture did not match", "IdentityMismatch"));
    }
}
