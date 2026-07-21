namespace BubblesBot.Bot.Systems;

public enum DamageEvidenceOutcome { Started, Waiting, DamageObserved, Blacklisted }

public readonly record struct DamageEvidenceResult(
    DamageEvidenceOutcome Outcome, uint TargetId, double EvidenceWindowMs, double ElapsedMs);

/// <summary>Deterministic accepted-attack to observed-damage correlation.</summary>
public sealed class DamageEvidenceTracker
{
    private uint _engagedId;
    private int _engagedHp;
    private TimeSpan _engagedSince;
    private readonly Dictionary<uint, TimeSpan> _blacklist = new();

    public uint EngagedId => _engagedId;
    public int BlacklistCount => _blacklist.Count;
    public double EngagedForMs(TimeSpan now) => _engagedId == 0 ? 0 : (now - _engagedSince).TotalMilliseconds;
    public IEnumerable<(uint Id, double RemainingMs)> Blacklist(TimeSpan now)
        => _blacklist.Select(x => (x.Key, Math.Max(0, (x.Value - now).TotalMilliseconds)));

    public void Tick(TimeSpan now)
    {
        if (_blacklist.Count == 0) return;
        var expired = _blacklist.Where(x => now >= x.Value).Select(x => x.Key).ToArray();
        foreach (var id in expired) _blacklist.Remove(id);
    }

    public bool IsBlacklisted(uint id, TimeSpan now)
    {
        Tick(now);
        return _blacklist.TryGetValue(id, out var until) && now < until;
    }

    public void ClearEngagement()
    {
        _engagedId = 0;
        _engagedSince = TimeSpan.Zero;
    }

    public DamageEvidenceResult ObserveAcceptedAttack(
        uint targetId, int hp, TimeSpan now, double evidenceWindowMs, double blacklistHoldMs)
    {
        Tick(now);
        if (targetId != _engagedId)
        {
            _engagedId = targetId;
            _engagedHp = hp;
            _engagedSince = now;
            return new(DamageEvidenceOutcome.Started, targetId, evidenceWindowMs, 0);
        }

        if (hp < _engagedHp)
        {
            _engagedHp = hp;
            _engagedSince = now;
            return new(DamageEvidenceOutcome.DamageObserved, targetId, evidenceWindowMs, 0);
        }

        var elapsed = (now - _engagedSince).TotalMilliseconds;
        if (elapsed < evidenceWindowMs)
            return new(DamageEvidenceOutcome.Waiting, targetId, evidenceWindowMs, elapsed);

        _blacklist[targetId] = now.Add(TimeSpan.FromMilliseconds(blacklistHoldMs));
        ClearEngagement();
        return new(DamageEvidenceOutcome.Blacklisted, targetId, evidenceWindowMs, elapsed);
    }

    public void Reset()
    {
        ClearEngagement();
        _blacklist.Clear();
    }
}
