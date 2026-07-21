using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Diagnostics;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

public readonly record struct BlightCoveragePriority(
    int UnguardedSectors, int NearbySectors, int UnguardedMarkers)
{
    public float Score => UnguardedSectors * 1000f
        + NearbySectors * 250f
        + Math.Min(UnguardedMarkers, 20) * 10f;
}

/// <summary>
/// Pump-local Blight tower planner/executor. It scores foundations by lane coverage, may walk
/// only far enough to put the label safely on screen, and yields immediately when the player
/// or target foundation becomes threatened. Build order is Freeze, Seismic, Meteor, then an
/// Empower tower that supports the cluster; later towers repeat that coverage pattern.
/// </summary>
public sealed class BlightTowerController
{
    private enum Phase
    {
        Idle,
        ApproachTarget,
        OpenBuildMenu,
        ClickBuild,
        VerifyBuild,
        OpenUpgradeMenu,
        ClickUpgrade,
        VerifyUpgrade,
    }

    private readonly record struct TowerState(
        uint Id, Vector2i Position, BlightTowerKind Kind, int Tier, string Path);
    private readonly record struct TowerEvidence(uint Id, string Path);

    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<EntityCache?> _getEntities;
    private readonly Dictionary<long, TimeSpan> _retryAfterByPosition = new();

    private Phase _phase;
    private uint _targetId;
    private Vector2i _targetPosition;
    private BlightTowerKind _targetKind;
    private int _menuIndex;
    private int _previousTier;
    private bool _targetIsUpgrade;
    private TimeSpan _phaseStartedAt = TimeSpan.MinValue;
    private TimeSpan _cooldownUntil = TimeSpan.MinValue;

    private const float EmpowerSupportRadius = 55f;
    private const float ImmediateThreatRadius = 25f;
    private const float TargetThreatRadius = 45f;
    private const float TowerEffectRadius = 45f;
    private static readonly int[] TierCosts = [150, 300, 450, 500];
    private const double ActionTimeoutSeconds = 5.0;
    private const double ApproachTimeoutSeconds = 12.0;
    private const int SafeTop = 80;
    private const int SafeBottom = 260;
    private const int SafeSide = 80;

    public BlightTowerController(Func<GameSnapshot?> getSnapshot, Func<EntityCache?> getEntities)
    {
        _getSnapshot = getSnapshot;
        _getEntities = getEntities;
    }

    public string Status { get; private set; } = "idle";
    public bool IsBusy => _phase != Phase.Idle;
    public Vector2i? NavigationGoal => _phase == Phase.ApproachTarget ? _targetPosition : null;
    public int? LastCurrency { get; private set; }

    /// <summary>Returns true while tower construction owns this behavior tick.</summary>
    public bool Tick(BehaviorContext ctx, Vector2i pump)
    {
        if (!ctx.Settings.BlightBuildTowers)
        {
            Reset();
            return false;
        }

        if (HasImmediateThreat(ctx)
            || IsBusy && HasThreatNear(ctx, _targetPosition, TargetThreatRadius))
        {
            if (IsBusy) Abort("hostile interrupted tower action");
            return false;
        }

        if (_phase != Phase.Idle && _phaseStartedAt != TimeSpan.MinValue)
        {
            var timeout = _phase == Phase.ApproachTarget
                ? ApproachTimeoutSeconds
                : ActionTimeoutSeconds;
            if ((BotMonotonicClock.Now - _phaseStartedAt).TotalSeconds > timeout)
            {
                Abort($"{_phase} timed out");
                return false;
            }
        }

        if (_phase == Phase.Idle)
        {
            if (BotMonotonicClock.Now < _cooldownUntil) return false;
            if (!TrySelectAction(ctx, pump)) return false;
        }

        return _phase switch
        {
            Phase.ApproachTarget => TickApproach(ctx),
            Phase.OpenBuildMenu => TickOpenMenu(ctx, upgrade: false),
            Phase.ClickBuild => TickClickBuild(ctx),
            Phase.VerifyBuild => TickVerifyBuild(ctx),
            Phase.OpenUpgradeMenu => TickOpenMenu(ctx, upgrade: true),
            Phase.ClickUpgrade => TickClickUpgrade(ctx),
            Phase.VerifyUpgrade => TickVerifyUpgrade(ctx),
            _ => false,
        };
    }

    public void Reset()
    {
        ResetAction();
        _retryAfterByPosition.Clear();
        _cooldownUntil = TimeSpan.MinValue;
        LastCurrency = null;
        Status = "idle";
    }

    /// <summary>Immediately relinquish navigation/click ownership to pump defense.</summary>
    public void PreemptForDefense()
    {
        if (IsBusy) Abort("pump defense preempted tower action");
        else Status = "holding for pump defense";
    }

    private bool TrySelectAction(BehaviorContext ctx, Vector2i pump)
    {
        // A tower label must never pull the character outside the defensive neighborhood.
        // The user setting remains an upper bound; the pump leash supplies the hard cap.
        var radius = EffectiveBuildRadius(
            ctx.Settings.BlightTowerBuildRadius,
            ctx.Settings.BlightDefendRadius,
            ctx.Settings.BlightTowerApproachDistance);
        var towers = CollectLocalTowers(ctx, pump, radius);
        var currency = ctx.Snapshot.BlightCurrency.Currency;
        LastCurrency = currency;
        if (currency is null)
            Status = "tower currency unavailable - defensive controls only";

        var foundations = ctx.Snapshot.GroundLabels
            .Where(label => label.Path.Contains("BlightFoundation", StringComparison.OrdinalIgnoreCase)
                && label.EntityGridPosition is { } position
                && Distance(position, pump) <= radius
                && !IsDeferred(position)
                && !HasThreatNear(ctx, position, TargetThreatRadius))
            .ToArray();
        var hasUnguardedPath = foundations.Any(label =>
            label.EntityGridPosition is { } position
            && CoveragePriority(ctx, position, towers, pump).UnguardedSectors > 0);

        // Finish a partially upgraded tower first. This makes disarm/restart and combat
        // interruptions recoverable without spending on a duplicate foundation. Breadth is
        // more valuable than depth, though: seed control on every unguarded path before
        // spending the same currency upgrading an already-covered lane.
        var incomplete = towers
            .Where(t => TargetTier(t.Kind) > t.Tier
                && !hasUnguardedPath
                && !IsDeferred(t.Position)
                && CanAffordNextStep(t.Kind, t.Tier, currency, ctx.Settings.BlightMeteorCurrencyReserve)
                && !HasThreatNear(ctx, t.Position, TargetThreatRadius))
            .OrderBy(t => KindPriority(t.Kind))
            .ThenBy(t => t.Tier)
            .ThenBy(t => Distance(t.Position, pump))
            .FirstOrDefault();
        if (incomplete.Id != 0)
        {
            SelectUpgrade(incomplete);
            return true;
        }

        if (HasReachedTowerLimit(towers.Count, ctx.Settings.BlightMaxTowers))
        {
            Status = $"local tower cap reached ({towers.Count}/{ctx.Settings.BlightMaxTowers})";
            return false;
        }

        if (foundations.Length == 0)
        {
            Status = $"no safe foundation within {radius:F0} of pump";
            return false;
        }

        foreach (var kind in CandidateKindOrder(towers))
        {
            if (!CanAffordNextStep(
                    kind, 0, currency, ctx.Settings.BlightMeteorCurrencyReserve))
                continue;
            var best = foundations
                .Where(label => CanPlace(
                    kind, label.EntityGridPosition!.Value, towers,
                    ctx.Settings.BlightMeteorBackfillPerControlPair))
                .Select(label => new
                {
                    Label = label,
                    Score = ScoreFoundation(ctx, label, kind, towers, pump),
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Label.EntityId)
                .FirstOrDefault();
            if (best?.Label.EntityGridPosition is not { } target) continue;

            _targetId = best.Label.EntityId;
            _targetPosition = target;
            _targetKind = kind;
            _menuIndex = BuildMenuIndex(kind);
            _previousTier = 0;
            _targetIsUpgrade = false;
            var status = $"selected {DisplayKind(kind)} foundation {_targetId} " +
                         $"pump={Distance(target, pump):F0} score={best.Score:F0} currency={currency}";
            Enter(IsSafeClickable(ctx, best.Label) ? Phase.OpenBuildMenu : Phase.ApproachTarget, status);
            EventLog.Emit("blight", "blight.tower-selected", EventSeverity.Info, Status,
                new Dictionary<string, object?>
                {
                    ["entityId"] = _targetId,
                    ["kind"] = DisplayKind(kind),
                    ["pumpDistance"] = Distance(target, pump),
                    ["score"] = best.Score,
                    ["localTowerCount"] = towers.Count,
                    ["requiresApproach"] = _phase == Phase.ApproachTarget,
                });
            return true;
        }

        Status = "no coverage-improving safe foundation";
        return false;
    }

    public static float EffectiveBuildRadius(
        float configuredRadius, float defendRadius, float approachDistance)
    {
        // FollowPath stops approachDistance short of the foundation. Capping the pad at
        // leash+approach keeps the character itself inside the pump leash even while building.
        var defensiveCap = Math.Max(40f, defendRadius + approachDistance);
        return Math.Clamp(MathF.Min(configuredRadius, defensiveCap), 40f, 200f);
    }

    private void SelectUpgrade(TowerState tower)
    {
        _targetId = tower.Id;
        _targetPosition = tower.Position;
        _targetKind = tower.Kind;
        _previousTier = tower.Tier;
        _targetIsUpgrade = true;
        var snapshot = _getSnapshot();
        var label = FindTowerAt(snapshot, tower.Position, tower.Kind);
        Enter(snapshot is not null && label is not null && IsSafeClickable(snapshot, label)
                ? Phase.OpenUpgradeMenu
                : Phase.ApproachTarget,
            $"resuming {DisplayKind(tower.Kind)} tower {tower.Id} at tier {tower.Tier}");
        EventLog.Emit("blight", "blight.tower-upgrade-resumed", EventSeverity.Info, Status,
            new Dictionary<string, object?>
            {
                ["entityId"] = tower.Id,
                ["kind"] = DisplayKind(tower.Kind),
                ["tier"] = tower.Tier,
                ["requiresApproach"] = _phase == Phase.ApproachTarget,
            });
    }

    private bool TickApproach(BehaviorContext ctx)
    {
        var label = FindTargetLabel(ctx.Snapshot, _targetIsUpgrade);
        if (label is not null && IsSafeClickable(ctx, label))
        {
            Enter(_targetIsUpgrade ? Phase.OpenUpgradeMenu : Phase.OpenBuildMenu,
                $"{DisplayKind(_targetKind)} label safely on screen");
            return true;
        }

        if (ctx.Live is { } live
            && Distance(live.GridPosition, _targetPosition) <= 10f)
        {
            Abort("target label unavailable after close approach");
            return false;
        }

        Status = $"approaching {DisplayKind(_targetKind)} label at {_targetPosition.X},{_targetPosition.Y}";
        return true;
    }

    private bool TickOpenMenu(BehaviorContext ctx, bool upgrade)
    {
        var label = FindTargetLabel(ctx.Snapshot, upgrade);
        if (label is null) { Abort("tower label disappeared before menu click"); return false; }
        var minimumButtons = upgrade && _previousTier >= 3 ? 2 : upgrade ? 1 : 6;
        if (label.TowerMenuButtons.Count >= minimumButtons)
        {
            Enter(upgrade ? Phase.ClickUpgrade : Phase.ClickBuild,
                $"{(upgrade ? "upgrade" : "build")} menu visible ({label.TowerMenuButtons.Count} buttons)");
            return true;
        }
        if (!IsSafeClickable(ctx, label))
        {
            Enter(Phase.ApproachTarget, "label left safe click zone");
            return true;
        }
        if (!ctx.Input.IsIdle) return true;

        var rect = label.LabelRect!.Value;
        var (x, y) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var id = label.EntityId;
        var ticket = ctx.Input.Click(x, y, ClickIntent.InteractUi,
            upgrade ? "open Blight tower upgrade menu" : "open Blight tower build menu",
            expectResolved: () => FindLabel(_getSnapshot(), id)?.TowerMenuButtons.Count >= minimumButtons,
            timeoutMs: 1200);
        if (ticket.Accepted) Status = $"clicked {(upgrade ? "tower" : "foundation")} label {id}";
        return true;
    }

    private bool TickClickBuild(BehaviorContext ctx)
    {
        var label = FindLabel(ctx.Snapshot, _targetId);
        if (label is null || label.TowerMenuButtons.Count <= _menuIndex)
        {
            Abort("build menu closed before selection");
            return false;
        }
        if (!ctx.Input.IsIdle) return true;
        var currency = ctx.Snapshot.BlightCurrency.Currency;
        LastCurrency = currency;
        if (!CanAffordNextStep(
                _targetKind, 0, currency, ctx.Settings.BlightMeteorCurrencyReserve))
            return WaitForCurrency(_targetKind, 0, currency, ctx.Settings.BlightMeteorCurrencyReserve);

        var rect = label.TowerMenuButtons[_menuIndex];
        var (x, y) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.Click(x, y, ClickIntent.InteractUi, $"build {DisplayKind(_targetKind)} tower",
            expectResolved: () => FindTowerEvidence(
                _getSnapshot(), _getEntities(), _targetPosition, _targetKind) is not null,
            timeoutMs: 1800);
        if (ticket.Accepted)
            Enter(Phase.VerifyBuild, $"clicked {DisplayKind(_targetKind)} build button index {_menuIndex}");
        return true;
    }

    private bool TickVerifyBuild(BehaviorContext ctx)
    {
        var tower = FindTowerEvidence(ctx.Snapshot, ctx.Entities, _targetPosition, _targetKind);
        if (tower is null) return true;

        _targetId = tower.Value.Id;
        _previousTier = ParseTier(tower.Value.Path);
        _targetIsUpgrade = true;
        EventLog.Emit("blight", "blight.tower-built", EventSeverity.Info,
            $"built {DisplayKind(_targetKind)} tower tier {_previousTier}",
            new Dictionary<string, object?> { ["entityId"] = _targetId, ["path"] = tower.Value.Path });
        if (_previousTier >= TargetTier(_targetKind))
        {
            Complete(_previousTier);
            return false;
        }
        Enter(Phase.OpenUpgradeMenu, $"built tier {_previousTier}; opening upgrade menu");
        return true;
    }

    private bool TickClickUpgrade(BehaviorContext ctx)
    {
        var tower = FindTowerAt(ctx.Snapshot, _targetPosition, _targetKind);
        if (tower is null || tower.TowerMenuButtons.Count == 0)
        {
            Abort("upgrade menu closed before selection");
            return false;
        }
        if (!ctx.Input.IsIdle) return true;

        _targetId = tower.EntityId;
        _previousTier = Math.Max(1, ParseTier(tower.Path));
        var currency = ctx.Snapshot.BlightCurrency.Currency;
        LastCurrency = currency;
        if (!CanAffordNextStep(
                _targetKind, _previousTier, currency, ctx.Settings.BlightMeteorCurrencyReserve))
            return WaitForCurrency(
                _targetKind, _previousTier, currency, ctx.Settings.BlightMeteorCurrencyReserve);
        var index = UpgradeMenuIndex(_targetKind, _previousTier, tower.TowerMenuButtons.Count);
        if (index < 0 || index >= tower.TowerMenuButtons.Count)
        {
            Abort($"no valid tier {_previousTier} upgrade for {DisplayKind(_targetKind)}");
            return false;
        }

        var rect = tower.TowerMenuButtons[index];
        var (x, y) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.Click(x, y, ClickIntent.InteractUi,
            _previousTier >= 3 ? "upgrade Fire tower to Meteor" : $"upgrade {DisplayKind(_targetKind)} tower",
            expectResolved: () => FindTowerEvidence(
                    _getSnapshot(), _getEntities(), _targetPosition, _targetKind) is { } live
                && ParseTier(live.Path) > _previousTier,
            timeoutMs: 1800);
        if (ticket.Accepted)
            Enter(Phase.VerifyUpgrade, $"clicked tier {_previousTier} upgrade index {index}");
        return true;
    }

    private bool TickVerifyUpgrade(BehaviorContext ctx)
    {
        var tower = FindTowerEvidence(ctx.Snapshot, ctx.Entities, _targetPosition, _targetKind);
        if (tower is null) return true;
        var tier = ParseTier(tower.Value.Path);
        if (tier <= _previousTier) return true;

        _targetId = tower.Value.Id;
        EventLog.Emit("blight", "blight.tower-upgraded", EventSeverity.Info,
            $"upgraded {DisplayKind(_targetKind)} tower to tier {tier}",
            new Dictionary<string, object?> { ["entityId"] = _targetId, ["path"] = tower.Value.Path });
        if (tier >= TargetTier(_targetKind))
        {
            Complete(tier);
            return false;
        }

        _previousTier = tier;
        Enter(Phase.OpenUpgradeMenu, $"tier {tier}; opening next upgrade menu");
        return true;
    }

    private IReadOnlyList<TowerState> CollectLocalTowers(BehaviorContext ctx, Vector2i pump, float radius)
    {
        var byPosition = new Dictionary<long, TowerState>();
        if (ctx.Entities is not null)
        {
            foreach (var entity in ctx.Entities.Entries.Values)
            {
                if (entity.IsStale || !IsTowerPath(entity.Path)
                    || Distance(entity.GridPosition, pump) > radius) continue;
                AddTower(byPosition, new TowerState(entity.Id, entity.GridPosition,
                    ClassifyPath(entity.Path), ParseTier(entity.Path), entity.Path));
            }
        }
        foreach (var label in ctx.Snapshot.GroundLabels)
        {
            if (!IsTowerPath(label.Path) || label.EntityGridPosition is not { } position
                || Distance(position, pump) > radius) continue;
            AddTower(byPosition, new TowerState(label.EntityId, position,
                ClassifyPath(label.Path), ParseTier(label.Path), label.Path));
        }
        return byPosition.Values.ToArray();
    }

    private static void AddTower(Dictionary<long, TowerState> byPosition, TowerState tower)
    {
        var key = PositionKey(tower.Position);
        if (!byPosition.TryGetValue(key, out var existing) || tower.Tier >= existing.Tier)
            byPosition[key] = tower;
    }

    private static IReadOnlyList<BlightTowerKind> CandidateKindOrder(IReadOnlyList<TowerState> towers)
    {
        var result = new List<BlightTowerKind>(4);
        var controls = new[] { BlightTowerKind.Chilling, BlightTowerKind.Seismic };
        foreach (var kind in controls)
            if (towers.All(t => t.Kind != kind)) result.Add(kind);

        var freezeCount = towers.Count(t => t.Kind == BlightTowerKind.Chilling);
        var seismicCount = towers.Count(t => t.Kind == BlightTowerKind.Seismic);
        var meteorCount = towers.Count(t => t.Kind == BlightTowerKind.Meteor);
        var completedControlPairs = Math.Min(freezeCount, seismicCount);
        var empowerCount = towers.Count(t => t.Kind == BlightTowerKind.Empowering);
        var desiredEmpower = (completedControlPairs + 1) / 2;
        foreach (var kind in controls
                     .OrderBy(kind => towers.Count(t => t.Kind == kind))
                     .ThenBy(KindPriority))
            if (!result.Contains(kind)) result.Add(kind);

        // Damage/support are backfill only after control coverage has been considered.
        if (completedControlPairs > 0
            && meteorCount >= completedControlPairs
            && empowerCount < desiredEmpower)
            result.Add(BlightTowerKind.Empowering);
        result.Add(BlightTowerKind.Meteor);
        if (!result.Contains(BlightTowerKind.Empowering))
            result.Add(BlightTowerKind.Empowering);
        return result;
    }

    private static bool CanPlace(
        BlightTowerKind kind,
        Vector2i position,
        IReadOnlyList<TowerState> towers,
        int meteorBackfillPerControlPair)
    {
        if (kind == BlightTowerKind.Empowering)
        {
            if (towers.Any(t => t.Kind == BlightTowerKind.Empowering
                    && Distance(t.Position, position) <= EmpowerSupportRadius * 1.5f))
                return false;
            return towers.Count(t => (t.Kind is BlightTowerKind.Chilling
                        or BlightTowerKind.Seismic
                        or BlightTowerKind.Meteor)
                    && Distance(t.Position, position) <= EmpowerSupportRadius) >= 2;
        }

        if (kind == BlightTowerKind.Meteor)
        {
            var pairs = Math.Min(
                towers.Count(t => t.Kind == BlightTowerKind.Chilling),
                towers.Count(t => t.Kind == BlightTowerKind.Seismic));
            var allowed = Math.Max(0, pairs * Math.Max(0, meteorBackfillPerControlPair));
            if (towers.Count(t => t.Kind == BlightTowerKind.Meteor) >= allowed)
                return false;
            // Damage backfill belongs inside an existing control envelope, but unlike the
            // non-stacking effects it may overlap other Meteors.
            if (!towers.Any(t => (t.Kind is BlightTowerKind.Chilling or BlightTowerKind.Seismic)
                    && Distance(t.Position, position) <= TowerEffectRadius * 1.6f))
                return false;
            return !towers.Any(t => t.Kind == BlightTowerKind.Meteor
                && Distance(t.Position, position) <= 16f);
        }

        return !towers.Any(t => t.Kind == kind
            && Distance(t.Position, position) <= TowerEffectRadius * 1.15f);
    }

    private static float ScoreFoundation(
        BehaviorContext ctx,
        GroundLabelView label,
        BlightTowerKind kind,
        IReadOnlyList<TowerState> towers,
        Vector2i pump)
    {
        var position = label.EntityGridPosition!.Value;
        var coverage = CoveragePriority(ctx, position, towers, pump);
        var laneScore = coverage.Score;
        if (ctx.Entities is not null)
        {
            foreach (var marker in ctx.Entities.Entries.Values)
            {
                if (string.IsNullOrEmpty(marker.Path)
                    || !marker.Path.Contains("BlightPathway", StringComparison.OrdinalIgnoreCase)
                    || Distance(marker.GridPosition, position) > TowerEffectRadius) continue;
                if (kind == BlightTowerKind.Meteor)
                {
                    var freezeCovered = towers.Any(t => t.Kind == BlightTowerKind.Chilling
                        && Distance(t.Position, marker.GridPosition) <= TowerEffectRadius);
                    var seismicCovered = towers.Any(t => t.Kind == BlightTowerKind.Seismic
                        && Distance(t.Position, marker.GridPosition) <= TowerEffectRadius);
                    if (freezeCovered && seismicCovered) laneScore += 5f;
                }
            }
        }

        var supportCount = towers.Count(t => (t.Kind is BlightTowerKind.Chilling
                or BlightTowerKind.Seismic
                or BlightTowerKind.Meteor)
            && Distance(t.Position, position) <= EmpowerSupportRadius);
        var supportScore = kind == BlightTowerKind.Empowering ? supportCount * 180f : 0f;
        var dangerScore = 0f;
        if (ctx.Entities is not null)
        {
            foreach (var hostile in ctx.Entities.Entries.Values)
            {
                if (!TargetEligibility.IsEligible(hostile)) continue;
                var distance = Distance(hostile.GridPosition, position);
                if (distance > 120f) continue;
                var rarity = hostile.Rarity switch
                {
                    EntityListReader.EntityRarity.Unique => 18f,
                    EntityListReader.EntityRarity.Rare => 9f,
                    EntityListReader.EntityRarity.Magic => 3f,
                    _ => 1f,
                };
                dangerScore += rarity * MathF.Max(0.2f, 1f - distance / 140f);
            }
        }
        var idealPumpDistance = MathF.Min(65f, ctx.Settings.BlightTowerBuildRadius * 0.65f);
        var placement = 100f - MathF.Abs(Distance(position, pump) - idealPumpDistance) * 1.25f;
        var playerDistance = ctx.Live is { } live ? Distance(position, live.GridPosition) : 100f;
        var visibleBonus = IsSafeClickable(ctx, label) ? 45f : 0f;
        return laneScore + supportScore + dangerScore + placement + visibleBonus - playerDistance * 0.08f;
    }

    private static BlightCoveragePriority CoveragePriority(
        BehaviorContext ctx, Vector2i foundation, IReadOnlyList<TowerState> towers, Vector2i pump)
    {
        var markers = ctx.Entities?.Entries.Values
            .Where(marker => !string.IsNullOrEmpty(marker.Path)
                && marker.Path.Contains("BlightPathway", StringComparison.OrdinalIgnoreCase))
            .Select(marker => marker.GridPosition)
            .ToArray() ?? [];
        var controls = towers
            .Where(tower => tower.Kind is BlightTowerKind.Chilling or BlightTowerKind.Seismic)
            .Select(tower => tower.Position)
            .ToArray();
        return CoveragePriority(foundation, pump, markers, controls, TowerEffectRadius);
    }

    public static BlightCoveragePriority CoveragePriority(
        Vector2i foundation,
        Vector2i pump,
        IReadOnlyList<Vector2i> pathwayMarkers,
        IReadOnlyList<Vector2i> controlTowers,
        float effectRadius = TowerEffectRadius)
    {
        var nearbyLaneSectors = new HashSet<int>();
        var unguardedLaneSectors = new HashSet<int>();
        var unguardedMarkers = 0;
        foreach (var marker in pathwayMarkers)
        {
            if (Distance(marker, foundation) > effectRadius) continue;
            var angle = MathF.Atan2(marker.Y - pump.Y, marker.X - pump.X);
            var sector = (int)MathF.Floor((angle + MathF.PI) / (MathF.PI / 6f));
            nearbyLaneSectors.Add(sector);
            if (controlTowers.Any(tower => Distance(tower, marker) <= effectRadius)) continue;
            unguardedLaneSectors.Add(sector);
            unguardedMarkers++;
        }
        return new BlightCoveragePriority(
            unguardedLaneSectors.Count, nearbyLaneSectors.Count, unguardedMarkers);
    }

    private GroundLabelView? FindTargetLabel(GameSnapshot snapshot, bool upgrade)
        => upgrade ? FindTowerAt(snapshot, _targetPosition, _targetKind) : FindLabel(snapshot, _targetId);

    private static GroundLabelView? FindLabel(GameSnapshot? snapshot, uint id)
        => snapshot?.GroundLabels.FirstOrDefault(label => label.EntityId == id);

    private static GroundLabelView? FindTowerAt(GameSnapshot? snapshot, Vector2i position, BlightTowerKind kind)
        => snapshot?.GroundLabels.FirstOrDefault(label => IsTowerPath(label.Path)
            && ClassifyPath(label.Path) == kind
            && label.EntityGridPosition is { } p
            && Distance(p, position) <= 4f);

    private static TowerEvidence? FindTowerEvidence(
        GameSnapshot? snapshot,
        EntityCache? entities,
        Vector2i position,
        BlightTowerKind kind)
    {
        var label = FindTowerAt(snapshot, position, kind);
        if (label is not null) return new TowerEvidence(label.EntityId, label.Path);
        if (entities is null) return null;
        foreach (var entity in entities.Entries.Values)
        {
            if (entity.IsStale || !IsTowerPath(entity.Path) || ClassifyPath(entity.Path) != kind)
                continue;
            if (Distance(entity.GridPosition, position) <= 4f)
                return new TowerEvidence(entity.Id, entity.Path);
        }
        return null;
    }

    private static bool IsSafeClickable(BehaviorContext ctx, GroundLabelView label)
        => IsSafeClickable(ctx.Snapshot, label);

    private static bool IsSafeClickable(GameSnapshot snapshot, GroundLabelView label)
    {
        if (!label.IsLabelVisible || !label.IsRectOnScreen || label.LabelRect is not { } rect) return false;
        return rect.CenterX >= SafeSide
            && rect.CenterX <= snapshot.Window.Width - SafeSide
            && rect.CenterY >= SafeTop
            && rect.CenterY <= snapshot.Window.Height - SafeBottom;
    }

    private static bool HasImmediateThreat(BehaviorContext ctx)
        => ctx.Live is { } live && HasThreatNear(ctx, live.GridPosition, ImmediateThreatRadius);

    private static bool HasThreatNear(BehaviorContext ctx, Vector2i position, float radius)
    {
        if (ctx.Entities is null) return false;
        foreach (var entity in ctx.Entities.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(entity)) continue;
            if (Distance(entity.GridPosition, position) <= radius) return true;
        }
        return false;
    }

    private void Enter(Phase phase, string status)
    {
        _phase = phase;
        _phaseStartedAt = BotMonotonicClock.Now;
        Status = status;
    }

    private void Complete(int tier)
    {
        Status = $"{DisplayKind(_targetKind)} tower complete at tier {tier}";
        _retryAfterByPosition.Remove(PositionKey(_targetPosition));
        ResetAction();
        _cooldownUntil = BotMonotonicClock.Now + TimeSpan.FromMilliseconds(1200);
    }

    private bool WaitForCurrency(
        BlightTowerKind kind, int currentTier, int? currency, int meteorReserve)
    {
        var cost = NextStepCost(currentTier);
        var reserve = kind == BlightTowerKind.Meteor ? Math.Max(0, meteorReserve) : 0;
        Status = currency is null
            ? $"waiting for tower currency before {DisplayKind(kind)} tier {currentTier + 1}"
            : $"waiting for {cost + reserve} currency ({currency} available; {reserve} reserved)";
        EventLog.Emit("blight", "blight.tower-budget-wait", EventSeverity.Info, Status,
            new Dictionary<string, object?>
            {
                ["kind"] = DisplayKind(kind),
                ["currentTier"] = currentTier,
                ["nextCost"] = cost,
                ["currency"] = currency,
                ["reservedCurrency"] = reserve,
            });
        ResetAction();
        _cooldownUntil = BotMonotonicClock.Now + TimeSpan.FromMilliseconds(750);
        return false;
    }

    private void Abort(string reason)
    {
        EventLog.Emit("blight", "blight.tower-action-aborted", EventSeverity.Warning, reason,
            new Dictionary<string, object?>
            {
                ["phase"] = _phase.ToString(),
                ["entityId"] = _targetId,
                ["kind"] = DisplayKind(_targetKind),
                ["position"] = $"{_targetPosition.X},{_targetPosition.Y}",
            });
        if (_phase != Phase.Idle)
            _retryAfterByPosition[PositionKey(_targetPosition)] =
                BotMonotonicClock.Now + TimeSpan.FromSeconds(12);
        ResetAction();
        _cooldownUntil = BotMonotonicClock.Now + TimeSpan.FromSeconds(2);
        Status = reason;
    }

    private void ResetAction()
    {
        _phase = Phase.Idle;
        _targetId = 0;
        _targetPosition = default;
        _targetKind = default;
        _menuIndex = 0;
        _previousTier = 0;
        _targetIsUpgrade = false;
        _phaseStartedAt = TimeSpan.MinValue;
    }

    private bool IsDeferred(Vector2i position)
        => _retryAfterByPosition.TryGetValue(PositionKey(position), out var until)
        && BotMonotonicClock.Now < until;

    public static bool IsTowerPath(string? path)
        => !string.IsNullOrEmpty(path)
        && path.Contains("BlightTower", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("TargetMarker", StringComparison.OrdinalIgnoreCase);

    public static BlightTowerKind ClassifyPath(string path)
    {
        if (path.Contains("Flamethrower", StringComparison.OrdinalIgnoreCase)) return BlightTowerKind.Damage;
        if (path.Contains("Meteor", StringComparison.OrdinalIgnoreCase)) return BlightTowerKind.Meteor;
        if (path.Contains("Chilling", StringComparison.OrdinalIgnoreCase)
            || path.Contains("FreezingTower", StringComparison.OrdinalIgnoreCase)
            || path.Contains("IcePrison", StringComparison.OrdinalIgnoreCase)) return BlightTowerKind.Chilling;
        if (path.Contains("Stunning", StringComparison.OrdinalIgnoreCase)
            || path.Contains("TemporalTower", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Petrification", StringComparison.OrdinalIgnoreCase)) return BlightTowerKind.Seismic;
        if (path.Contains("Buff", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Empower", StringComparison.OrdinalIgnoreCase)) return BlightTowerKind.Empowering;
        if (path.Contains("Fire", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Flame", StringComparison.OrdinalIgnoreCase)) return BlightTowerKind.Meteor;
        if (path.Contains("Minion", StringComparison.OrdinalIgnoreCase)) return BlightTowerKind.Summoning;
        return BlightTowerKind.Damage;
    }

    public static int ParseTier(string path)
    {
        if (path.Contains("Meteor", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Flamethrower", StringComparison.OrdinalIgnoreCase)
            || path.Contains("FreezingTower", StringComparison.OrdinalIgnoreCase)
            || path.Contains("IcePrison", StringComparison.OrdinalIgnoreCase)
            || path.Contains("TemporalTower", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Petrification", StringComparison.OrdinalIgnoreCase)
            || path.Contains("BuffPlayersTower", StringComparison.OrdinalIgnoreCase)
            || path.Contains("WeakenEnemiesTower", StringComparison.OrdinalIgnoreCase))
            return 4;
        var rank = path.IndexOf("Rank", StringComparison.OrdinalIgnoreCase);
        if (rank >= 0 && rank + 4 < path.Length && char.IsDigit(path[rank + 4]))
            return path[rank + 4] - '0';
        return 1;
    }

    public static int TargetTier(BlightTowerKind kind)
        => kind == BlightTowerKind.Meteor ? 4 : 3;

    /// <summary>Zero disables the optional emergency/debug cap; positive values cap towers.</summary>
    public static bool HasReachedTowerLimit(int towerCount, int configuredLimit)
        => configuredLimit > 0 && towerCount >= configuredLimit;

    public static int NextStepCost(int currentTier)
        => currentTier >= 0 && currentTier < TierCosts.Length
            ? TierCosts[currentTier]
            : int.MaxValue;

    public static bool CanAffordNextStep(
        BlightTowerKind kind, int currentTier, int? currency, int meteorReserve)
    {
        // A missing HUD read must never unlock discretionary Meteor spending because its
        // reserve cannot be proven. Defensive controls may still be attempted: PoE disables
        // unaffordable radial buttons, and every build/upgrade click has an entity/tier
        // postcondition, so this degrades to a harmless deferred retry instead of no towers.
        if (currency is null) return kind != BlightTowerKind.Meteor;
        var cost = NextStepCost(currentTier);
        if (cost == int.MaxValue) return false;
        var reserve = kind == BlightTowerKind.Meteor ? Math.Max(0, meteorReserve) : 0;
        return currency.Value >= cost + reserve;
    }

    public static int UpgradeMenuIndex(BlightTowerKind kind, int currentTier, int buttonCount)
    {
        if (buttonCount <= 0) return -1;
        if (kind == BlightTowerKind.Meteor && currentTier >= 3)
            return buttonCount >= 2 ? 1 : -1; // AutoExile Fire/Left branch => Meteor.
        return 0;
    }

    public static int BuildMenuIndex(BlightTowerKind kind) => kind switch
    {
        BlightTowerKind.Chilling => 0,
        BlightTowerKind.Empowering => 2,
        BlightTowerKind.Seismic => 3,
        BlightTowerKind.Summoning => 4,
        BlightTowerKind.Meteor => 5,
        _ => 5,
    };

    private static int KindPriority(BlightTowerKind kind) => kind switch
    {
        BlightTowerKind.Chilling => 0,
        BlightTowerKind.Seismic => 1,
        BlightTowerKind.Meteor => 2,
        BlightTowerKind.Empowering => 3,
        _ => 4,
    };

    private static string DisplayKind(BlightTowerKind kind) => kind switch
    {
        BlightTowerKind.Chilling => "Freeze",
        BlightTowerKind.Meteor => "Meteor",
        BlightTowerKind.Empowering => "Empower",
        _ => kind.ToString(),
    };

    private static long PositionKey(Vector2i position)
        => ((long)position.X << 32) | (uint)position.Y;

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
