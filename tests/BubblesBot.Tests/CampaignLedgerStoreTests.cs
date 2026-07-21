using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class CampaignLedgerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "BubblesBot.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTripsRotationAndCredits()
    {
        var store = new CampaignLedgerStore(_dir);
        var ledger = CampaignLedger.Empty("strat1", CampaignMode.GuardianRotation, ["A", "B", "C", "D"])
            with { Credited = [new CampaignCredit("A", "run1", "witness", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc))] };

        store.Save(ledger);
        var loaded = store.Load("strat1");

        Assert.NotNull(loaded);
        Assert.Equal(CampaignMode.GuardianRotation, loaded.Mode);
        Assert.Equal(4, loaded.Rotation.Length);
        Assert.Single(loaded.Credited);
        Assert.Equal("A", loaded.Credited[0].MapName);
    }

    [Fact]
    public void RemainingAndCompleteReflectCredits()
    {
        var ledger = CampaignLedger.Empty("s", CampaignMode.GuardianRotation, ["A", "B"]);
        Assert.Equal(new[] { "A", "B" }, ledger.Remaining());
        Assert.False(ledger.RotationComplete);

        var progressed = ledger with
        {
            Credited =
            [
                new CampaignCredit("A", "r", "w", default),
                new CampaignCredit("B", "r", "w", default),
            ],
        };
        Assert.Empty(progressed.Remaining());
        Assert.True(progressed.RotationComplete);
    }

    [Fact]
    public void ReconcileDropsCreditsForMapsNoLongerInRotation()
    {
        var ledger = CampaignLedger.Empty("s", CampaignMode.GuardianRotation, ["A", "B"])
            with { Credited = [new CampaignCredit("A", "r", "w", default), new CampaignCredit("OLD", "r", "w", default)] };

        var reconciled = CampaignLedgerStore.Reconcile(ledger, ["A", "B", "C"]);

        Assert.Equal(3, reconciled.Rotation.Length);
        Assert.Single(reconciled.Credited);
        Assert.Equal("A", reconciled.Credited[0].MapName);
    }

    [Fact]
    public void CorruptLedgerLoadsAsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "campaign-strat1.json"), "not json");
        Assert.Null(new CampaignLedgerStore(_dir).Load("strat1"));
    }

    [Fact]
    public void DeleteRemovesLedger()
    {
        var store = new CampaignLedgerStore(_dir);
        store.Save(CampaignLedger.Empty("s", CampaignMode.GuardianRotation, ["A"]));
        store.Delete("s");
        Assert.Null(store.Load("s"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
