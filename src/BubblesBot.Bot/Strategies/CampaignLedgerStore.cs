using System.Text.Json;

namespace BubblesBot.Bot.Strategies;

/// <summary>One credited map within a multi-map campaign (e.g. a Guardian-rotation member).</summary>
public sealed record CampaignCredit(string MapName, string RunId, string WitnessEvidence, DateTime TimestampUtc);

/// <summary>
/// Durable multi-map campaign progress (Guardian rotations + Maven invitations). Persisted so a
/// four-map rotation survives process restarts; reconciled against live evidence before any
/// credit is trusted after a restart. <b>Execution is deferred</b> — this store defines the shape
/// and persistence; running a rotation needs Maven-witness reads and invitation rolling that this
/// build does not have yet, so <c>campaign.mode != none</c> still fails strategy validation.
/// </summary>
public sealed record CampaignLedger(
    string StrategyId,
    CampaignMode Mode,
    string[] Rotation,              // the four map-family members, in order
    CampaignCredit[] Credited,      // proven completions this cycle
    string InvitationState,         // none | rolling | ready | activated | complete
    DateTime UpdatedUtc)
{
    public static CampaignLedger Empty(string strategyId, CampaignMode mode, IEnumerable<string> rotation)
        => new(strategyId, mode, rotation.ToArray(), [], "none", default);

    /// <summary>Maps in the rotation not yet credited this cycle.</summary>
    public IEnumerable<string> Remaining()
        => Rotation.Where(m => !Credited.Any(c => c.MapName.Equals(m, StringComparison.OrdinalIgnoreCase)));

    public bool RotationComplete => Rotation.Length > 0 && !Remaining().Any();
}

/// <summary>
/// Atomic, fail-closed persistence for <see cref="CampaignLedger"/>, one file per strategy under
/// <c>%LOCALAPPDATA%/BubblesBot/run-state/campaign-&lt;strategyId&gt;.json</c>. Mirrors
/// <c>SimulacrumRecoveryStore</c>: tmp+move writes, corrupt/interrupted data loads as null and the
/// caller re-derives from live evidence rather than crashing.
/// </summary>
public sealed class CampaignLedgerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;

    public CampaignLedgerStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubblesBot", "run-state");
    }

    public void Save(CampaignLedger ledger)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(ledger.StrategyId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(ledger with { UpdatedUtc = ledger.UpdatedUtc }, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public CampaignLedger? Load(string strategyId)
    {
        try
        {
            var path = PathFor(strategyId);
            if (!File.Exists(path)) return null;
            var ledger = JsonSerializer.Deserialize<CampaignLedger>(File.ReadAllText(path), JsonOptions);
            return ledger?.StrategyId == strategyId ? ledger : null;
        }
        catch
        {
            return null;   // corrupt/interrupted — caller re-derives from live evidence
        }
    }

    public void Delete(string strategyId)
    {
        try
        {
            var path = PathFor(strategyId);
            if (File.Exists(path)) File.Delete(path);
            var temp = path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch { /* a later Save/Delete retries */ }
    }

    /// <summary>
    /// Reconcile a loaded ledger against live evidence: drop credits whose maps are no longer in
    /// the rotation (a strategy edit), so stale process/file state can never grant completion
    /// credit that live reality doesn't support.
    /// </summary>
    public static CampaignLedger Reconcile(CampaignLedger ledger, IEnumerable<string> liveRotation)
    {
        var rotation = liveRotation.ToArray();
        var valid = ledger.Credited
            .Where(c => rotation.Any(m => m.Equals(c.MapName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return ledger with { Rotation = rotation, Credited = valid };
    }

    private string PathFor(string strategyId)
    {
        var safe = string.Concat(strategyId.Where(char.IsLetterOrDigit));
        if (safe.Length == 0) safe = "default";
        return Path.Combine(_directory, $"campaign-{safe}.json");
    }
}
