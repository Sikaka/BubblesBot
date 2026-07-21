using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class StrategyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "BubblesBot.Tests", Guid.NewGuid().ToString("N"));

    private StrategyStore NewStore() => new(_directory);

    private static FarmingStrategy Valid(string name = "Test strategy")
    {
        var strategy = LegacySettingsMigration.CloisterStackedDecks(new LegacyFarmSettings());
        strategy.Identity.Name = name;
        return strategy;
    }

    [Fact]
    public void SavePersistsAndReloadsAcrossStoreInstances()
    {
        var store = NewStore();
        var strategy = Valid();
        var result = store.Save(strategy);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_directory, strategy.Identity.Id + ".json")));

        var reloaded = NewStore();
        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.TryGet(strategy.Identity.Id, out var loaded));
        Assert.Equal("Test strategy", loaded.Identity.Name);
        Assert.NotEqual(default, loaded.Identity.CreatedUtc);
        Assert.NotEqual(default, loaded.Identity.ModifiedUtc);
    }

    [Fact]
    public void SaveAssignsIdWhenBlank()
    {
        var store = NewStore();
        var strategy = Valid();
        strategy.Identity.Id = "";
        store.Save(strategy);
        Assert.False(string.IsNullOrWhiteSpace(strategy.Identity.Id));
    }

    [Fact]
    public void ActivatePublishesSnapshotAndBumpsVersion()
    {
        var store = NewStore();
        var strategy = Valid();
        store.Save(strategy);
        var before = store.Version;

        var result = store.Activate(strategy.Identity.Id);

        Assert.True(result.Ok);
        Assert.NotNull(store.Active);
        Assert.Equal(strategy.Identity.Id, store.ActiveId);
        Assert.True(store.Version > before);
    }

    [Fact]
    public void ActivateUnknownIdFailsWithoutChangingActive()
    {
        var store = NewStore();
        var result = store.Activate("does-not-exist");
        Assert.False(result.Ok);
        Assert.Null(store.Active);
    }

    [Fact]
    public void ActivateInvalidStrategyFailsClosed()
    {
        var store = NewStore();
        var strategy = Valid();
        strategy.Campaign.Mode = CampaignMode.GuardianRotation;
        var saved = store.Save(strategy);   // drafts with errors may be saved…
        Assert.False(saved.Ok);

        var result = store.Activate(strategy.Identity.Id);   // …but never activated
        Assert.False(result.Ok);
        Assert.Null(store.Active);
    }

    [Fact]
    public void SavingActiveStrategyIntoInvalidStateDeactivatesIt()
    {
        var store = NewStore();
        var strategy = Valid();
        store.Save(strategy);
        store.Activate(strategy.Identity.Id);

        strategy.Campaign.Mode = CampaignMode.GuardianRotation;   // still unsupported
        var result = store.Save(strategy);

        Assert.False(result.Ok);
        Assert.Null(store.Active);
    }

    [Fact]
    public void DeleteActiveThrowsUntilDeactivated()
    {
        var store = NewStore();
        var strategy = Valid();
        store.Save(strategy);
        store.Activate(strategy.Identity.Id);

        Assert.Throws<InvalidOperationException>(() => store.Delete(strategy.Identity.Id));

        store.Deactivate();
        store.Delete(strategy.Identity.Id);
        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(Path.Combine(_directory, strategy.Identity.Id + ".json")));
    }

    [Fact]
    public void ImportStoresValidDocumentInactive()
    {
        var store = NewStore();
        var json = StrategySerialization.Serialize(Valid("Imported"));

        var result = store.Import(json);

        Assert.NotNull(result.Strategy);
        Assert.True(result.Validation.Ok);
        Assert.Equal(1, store.Count);
        Assert.Null(store.Active);
    }

    [Fact]
    public void ImportRegeneratesCollidingId()
    {
        var store = NewStore();
        var original = Valid("Original");
        store.Save(original);

        var result = store.Import(StrategySerialization.Serialize(original));

        Assert.NotNull(result.Strategy);
        Assert.NotEqual(original.Identity.Id, result.Strategy!.Identity.Id);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ImportRejectsUnknownMechanicTypeWithoutStoring()
    {
        var store = NewStore();
        var json = StrategySerialization.Serialize(Valid())
            .Replace("\"type\": \"shrines\"", "\"type\": \"harvest\"");

        var result = store.Import(json);

        Assert.Null(result.Strategy);
        Assert.False(result.Validation.Ok);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ImportRejectsValidationErrorsWithoutStoring()
    {
        var store = NewStore();
        var strategy = Valid();
        strategy.Campaign.Mode = CampaignMode.GuardianRotation;

        var result = store.Import(StrategySerialization.Serialize(strategy));

        Assert.Null(result.Strategy);
        Assert.False(result.Validation.Ok);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ExportReturnsStoredFileVerbatim()
    {
        var store = NewStore();
        var strategy = Valid();
        store.Save(strategy);

        var exported = store.Export(strategy.Identity.Id);

        Assert.Equal(File.ReadAllText(Path.Combine(_directory, strategy.Identity.Id + ".json")), exported);
        var roundTripped = StrategySerialization.Parse(exported);
        Assert.Equal(strategy.Identity.Name, roundTripped.Identity.Name);
    }

    [Fact]
    public void CorruptFileIsSkippedAndReported()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "not json at all");
        var good = Valid("Survivor");
        new StrategyStore(_directory).Save(good);

        var store = NewStore();

        Assert.Equal(1, store.Count);
        Assert.Contains(store.LoadErrors, error => error.Contains("broken.json"));
    }

    [Fact]
    public void StoreSnapshotsDoNotAliasCallerObjects()
    {
        var store = NewStore();
        var strategy = Valid();
        store.Save(strategy);
        store.Activate(strategy.Identity.Id);

        strategy.Identity.Name = "mutated after save";

        Assert.True(store.TryGet(strategy.Identity.Id, out var stored));
        Assert.Equal("Test strategy", stored.Identity.Name);
        Assert.Equal("Test strategy", store.Active!.Identity.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
