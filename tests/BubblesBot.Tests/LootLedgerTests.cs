using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class LootLedgerTests
{
    [Fact]
    public void ConfirmedStackUsesConservativeAndPlausibleTotals()
    {
        var ledger = new LootLedger();

        ledger.Record("Orb of Alteration", 5,
            new LootEvaluation(true, "priced", 0.2f, "currency", 0.2f));
        ledger.Record("Unidentified unique", 1,
            new LootEvaluation(true, "shared art", 1f, "unique", 100f));

        var snapshot = ledger.Snapshot();
        Assert.Equal(2, snapshot.Pickups);
        Assert.Equal(2f, snapshot.TotalChaos, 3);
        Assert.Equal(101f, snapshot.MaxPlausibleChaos, 3);
        Assert.Equal(1f, snapshot.ByCategory["currency"], 3);
        Assert.Equal(1f, snapshot.ByCategory["unique"], 3);
        Assert.Equal("Unidentified unique", snapshot.Recent[0].Name);
        Assert.Equal(5, snapshot.Recent[1].StackCount);
        Assert.True(snapshot.ChaosPerHour > 0);
    }
}
