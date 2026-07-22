using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Tick-driven, bounded currency transaction for carried Shaper Guardian keys and the staged
/// Formed invitation. Every source and target is hover-owned before a click, and activation
/// is allowed only after the final tooltip is readable and policy-safe.
/// </summary>
public sealed class GuardianItemRollController
{
    public enum TargetKind { GuardianMaps, FormedInvitation }
    public enum Result { InProgress, Succeeded, Failed }
    private enum Step { Idle, FindTarget, HoverTarget, Decide, HoverCurrency, ApplyCurrency, Done, Failed }

    public const string GuardianMapPath = "Metadata/Items/Maps/MapKeyShaperGuardian";
    private const string WisdomPath = "Metadata/Items/Currency/CurrencyIdentification";
    private const string ScourPath = "Metadata/Items/Currency/CurrencyConvertToNormal";
    private const string AlchemyPath = "Metadata/Items/Currency/CurrencyUpgradeToRare";
    private const string ChaosPath = "Metadata/Items/Currency/CurrencyRerollRare";
    private const int InventoryKeyVk = 0x49;
    private const int HoverSettleMs = 180;

    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<IEnumerable<string>?> _getModifierPolicyOverrides;
    private Step _step;
    private TargetKind _kind;
    private readonly HashSet<string> _completedSlots = [];
    private ElementGeometry.Rect _targetSlot;
    private nint _targetElement;
    private string _currencyPath = string.Empty;
    private string _currencyName = string.Empty;
    private nint _currencyElement;
    private ElementGeometry.Rect _currencyRect;
    private int _currencyBefore;
    private string _fingerprintBefore = string.Empty;
    private TimeSpan _hoveredAt;
    private int _chaosUsedOnTarget;
    private int _mapsCompleted;
    private int _mapsExpected;
    private GuardianInvitationState _tooltip;
    private EntityListReader.EntityRarity _rarity;
    private bool _identified;
    private int _quantity;
    private int _minQuantity;
    private int _preferredQuantity;
    private int _maxChaos;

    public string Status { get; private set; } = "idle";
    public int MapsCompleted => _mapsCompleted;
    public int MapsExpected => _mapsExpected;
    public int FinalQuantity => _quantity;

    public GuardianItemRollController(
        Func<GameSnapshot?> getSnapshot,
        Func<IEnumerable<string>?> getModifierPolicyOverrides)
    {
        _getSnapshot = getSnapshot;
        _getModifierPolicyOverrides = getModifierPolicyOverrides;
    }

    public void StartMaps(int maxChaos)
    {
        Reset();
        _kind = TargetKind.GuardianMaps;
        _maxChaos = Math.Max(1, maxChaos);
        _step = Step.FindTarget;
        Status = "preparing carried Guardian maps";
    }

    public void StartInvitation(int minQuantity, int preferredQuantity, int maxChaos)
    {
        Reset();
        _kind = TargetKind.FormedInvitation;
        _minQuantity = Math.Max(0, minQuantity);
        _preferredQuantity = Math.Max(_minQuantity, preferredQuantity);
        _maxChaos = Math.Max(1, maxChaos);
        _step = Step.FindTarget;
        Status = "preparing The Formed";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (_step == Step.Done) return Result.Succeeded;
        if (_step == Step.Failed) return Result.Failed;
        if (_step == Step.Idle) return Fail("rolling controller was not started");

        if (!ctx.Snapshot.Inventory.IsOpen)
        {
            ctx.Input.VerifiedTapKey(
                InventoryKeyVk, ClickIntent.InteractUi, "open inventory for Guardian rolling",
                expectResolved: () => _getSnapshot()?.Inventory.IsOpen ?? false,
                timeoutMs: 1500);
            Status = "opening inventory for rolling currency";
            return Result.InProgress;
        }

        return _step switch
        {
            Step.FindTarget => TickFindTarget(ctx),
            Step.HoverTarget => TickHoverTarget(ctx),
            Step.Decide => TickDecide(ctx),
            Step.HoverCurrency => TickHoverCurrency(ctx),
            Step.ApplyCurrency => TickApplyCurrency(ctx),
            _ => Result.InProgress,
        };
    }

    private Result TickFindTarget(BehaviorContext ctx)
        => FindTarget(ctx);

    private Result FindTarget(BehaviorContext ctx)
    {
        if (_kind == TargetKind.GuardianMaps)
        {
            var maps = ctx.Snapshot.Inventory.Items
                .Where(x => x.Path.Equals(GuardianMapPath, StringComparison.OrdinalIgnoreCase)
                    && x.Rect is not null)
                .OrderBy(x => x.Rect!.Value.Y).ThenBy(x => x.Rect!.Value.X).ToArray();
            _mapsExpected = Math.Max(_mapsExpected, maps.Length);
            var target = maps.FirstOrDefault(x => !_completedSlots.Contains(SlotKey(x.Rect!.Value)));
            if (target.ItemEntity == 0)
            {
                _step = Step.Done;
                Status = $"all {_mapsCompleted} carried Guardian maps are runnable";
                return Result.Succeeded;
            }
            _targetSlot = target.Rect!.Value;
            _targetElement = target.ElementAddress;
        }
        else
        {
            var target = ctx.Snapshot.MapReceptacle.Item();
            if (target is not { Rect: { } rect }
                || !target.Value.BaseName.Contains("The Formed", StringComparison.OrdinalIgnoreCase))
                return Fail("The Formed is not staged in the visible Map Receptacle");
            _targetSlot = rect;
            _targetElement = target.Value.Element;
        }
        _hoveredAt = TimeSpan.MinValue;
        _step = Step.HoverTarget;
        return Result.InProgress;
    }

    private Result TickHoverTarget(BehaviorContext ctx)
    {
        // Currency application can rebuild the NormalInventoryItem element in place (most
        // visibly when Wisdom flips unidentified -> identified). Refresh the slot-owned
        // element before proving hover ancestry; the pre-roll element address is stale.
        var refreshed = CurrentTarget(ctx.Snapshot);
        if (refreshed.Element == 0)
            return Fail("rolling target disappeared before tooltip inspection");
        _targetElement = refreshed.Element;
        _targetSlot = refreshed.Rect;
        var point = ctx.Snapshot.Window.ToScreen(_targetSlot.CenterX, _targetSlot.CenterY);
        ctx.Input.HoverAt(point.X, point.Y, CursorPriority.CombatAim);
        if (_hoveredAt == TimeSpan.MinValue)
        {
            _hoveredAt = BotMonotonicClock.Now;
            Status = "reading item tooltip";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _hoveredAt).TotalMilliseconds < HoverSettleMs)
            return Result.InProgress;

        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        if (!HoverOwns(ctx.Snapshot, hover.Element, _targetElement) || hover.TooltipLines.Count == 0)
            return Fail("item tooltip ownership could not be proven");
        _tooltip = GuardianInvitationPolicy.EvaluateTooltip(
            hover.TooltipLines, _getModifierPolicyOverrides());
        if (_kind == TargetKind.GuardianMaps)
        {
            var item = FindMapAtSlot(ctx.Snapshot.Inventory, _targetSlot);
            if (item.ItemEntity == 0) return Fail("Guardian map moved during inspection");
            _rarity = item.Rarity;
            _identified = item.Identified;
            _quantity = _tooltip.ItemQuantity >= 0 ? _tooltip.ItemQuantity
                : item.Rarity == EntityListReader.EntityRarity.Normal ? 0 : -1;
        }
        else
        {
            var item = ctx.Snapshot.MapReceptacle.Item();
            if (item is null || !_tooltip.IsTheFormed) return Fail("staged invitation identity changed");
            _rarity = item.Value.Rarity;
            _identified = true;
            _quantity = _tooltip.ItemQuantity >= 0 ? _tooltip.ItemQuantity
                : item.Value.Rarity == EntityListReader.EntityRarity.Normal ? 0 : -1;
        }
        _step = Step.Decide;
        return Result.InProgress;
    }

    private Result TickDecide(BehaviorContext ctx)
    {
        if (_kind == TargetKind.GuardianMaps && !_identified)
            return ChooseCurrency(ctx, WisdomPath, "Scroll of Wisdom");
        if (_rarity == EntityListReader.EntityRarity.Magic)
        {
            if (CurrencyCount(ctx.Snapshot.Inventory, AlchemyPath) < 1)
                return Fail("a Magic item requires both an Orb of Scouring and an Orb of Alchemy");
            return ChooseCurrency(ctx, ScourPath, "Orb of Scouring");
        }
        if (_rarity == EntityListReader.EntityRarity.Normal)
        {
            if (_kind == TargetKind.FormedInvitation
                && CurrencyCount(ctx.Snapshot.Inventory, ChaosPath) < _maxChaos)
                return Fail($"The Formed roll requires a {_maxChaos}-Chaos reserve before Alchemy");
            return ChooseCurrency(ctx, AlchemyPath, "Orb of Alchemy");
        }
        if (_rarity != EntityListReader.EntityRarity.Rare)
            return Fail($"unsupported item rarity {_rarity}");

        var quantityRequired = _kind == TargetKind.FormedInvitation && _quantity < _minQuantity;
        if (_tooltip.HasForbiddenModifier || quantityRequired)
        {
            if (_kind == TargetKind.FormedInvitation && _chaosUsedOnTarget == 0
                && CurrencyCount(ctx.Snapshot.Inventory, ChaosPath) < _maxChaos)
                return Fail($"The Formed reroll requires {_maxChaos} visible Chaos Orbs");
            if (_chaosUsedOnTarget >= _maxChaos)
                return Fail($"reroll budget exhausted at {_quantity}% quantity");
            return ChooseCurrency(ctx, ChaosPath, "Chaos Orb");
        }

        if (_kind == TargetKind.GuardianMaps)
        {
            _completedSlots.Add(SlotKey(_targetSlot));
            _mapsCompleted++;
            _chaosUsedOnTarget = 0;
            _step = Step.FindTarget;
            Status = $"Guardian maps safe: {_mapsCompleted}/{_mapsExpected}";
            return Result.InProgress;
        }

        _step = Step.Done;
        Status = _quantity >= _preferredQuantity
            ? $"The Formed is safe at preferred {_quantity}% quantity"
            : $"The Formed is safe at accepted {_quantity}% quantity";
        return Result.Succeeded;
    }

    private Result ChooseCurrency(BehaviorContext ctx, string path, string name)
    {
        var currency = ctx.Snapshot.Inventory.Items
            .Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && x.Rect is not null)
            .OrderByDescending(x => x.StackSize).FirstOrDefault();
        if (currency.ItemEntity == 0 || currency.Rect is not { } rect)
            return Fail($"missing {name}");
        _currencyPath = path;
        _currencyName = name;
        _currencyElement = currency.ElementAddress;
        _currencyRect = rect;
        _hoveredAt = TimeSpan.MinValue;
        _step = Step.HoverCurrency;
        return Result.InProgress;
    }

    private Result TickHoverCurrency(BehaviorContext ctx)
    {
        var point = ctx.Snapshot.Window.ToScreen(_currencyRect.CenterX, _currencyRect.CenterY);
        ctx.Input.HoverAt(point.X, point.Y, CursorPriority.CombatAim);
        if (_hoveredAt == TimeSpan.MinValue)
        {
            _hoveredAt = BotMonotonicClock.Now;
            Status = $"verifying {_currencyName}";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _hoveredAt).TotalMilliseconds < HoverSettleMs)
            return Result.InProgress;
        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        if (!HoverOwns(ctx.Snapshot, hover.Element, _currencyElement)
            || !hover.TooltipLines.Any(x => x.Contains(_currencyName, StringComparison.OrdinalIgnoreCase)))
            return Fail($"{_currencyName} source identity could not be proven");

        var ticket = ctx.Input.RightClick(
            point.X, point.Y, ClickIntent.InteractUi, $"select {_currencyName}",
            expectResolved: () => _getSnapshot()?.Cursor.Action == CursorView.CursorAction.UseItem,
            timeoutMs: 2000);
        if (ticket.Accepted)
        {
            _hoveredAt = TimeSpan.MinValue;
            _step = Step.ApplyCurrency;
        }
        return Result.InProgress;
    }

    private Result TickApplyCurrency(BehaviorContext ctx)
    {
        if (ctx.Snapshot.Cursor.Action != CursorView.CursorAction.UseItem)
            return Result.InProgress;
        var liveTarget = CurrentTarget(ctx.Snapshot);
        if (liveTarget.Element == 0) return Fail("rolling target disappeared");
        _targetElement = liveTarget.Element;
        _targetSlot = liveTarget.Rect;
        var point = ctx.Snapshot.Window.ToScreen(_targetSlot.CenterX, _targetSlot.CenterY);
        ctx.Input.HoverAt(point.X, point.Y, CursorPriority.CombatAim);
        if (_hoveredAt == TimeSpan.MinValue)
        {
            _hoveredAt = BotMonotonicClock.Now;
            _currencyBefore = CurrencyCount(ctx.Snapshot.Inventory, _currencyPath);
            _fingerprintBefore = liveTarget.Fingerprint;
            Status = $"applying {_currencyName}";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _hoveredAt).TotalMilliseconds < HoverSettleMs)
            return Result.InProgress;
        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        if (!HoverOwns(ctx.Snapshot, hover.Element, _targetElement))
            return Fail("currency target identity could not be proven");

        var currencyPath = _currencyPath;
        var beforeCount = _currencyBefore;
        var beforeFingerprint = _fingerprintBefore;
        var ticket = ctx.Input.Click(
            point.X, point.Y, ClickIntent.InteractUi, $"apply {_currencyName}",
            expectResolved: () =>
            {
                var snap = _getSnapshot();
                if (snap is null) return false;
                var current = CurrentTarget(snap);
                return current.Element != 0
                    && CurrencyCount(snap.Inventory, currencyPath) == beforeCount - 1
                    && current.Fingerprint != beforeFingerprint;
            }, timeoutMs: 3000);
        if (ticket.Accepted)
        {
            if (_currencyPath == ChaosPath) _chaosUsedOnTarget++;
            _hoveredAt = TimeSpan.MinValue;
            _step = Step.HoverTarget;
        }
        return Result.InProgress;
    }

    public void Reset()
    {
        _step = Step.Idle;
        _completedSlots.Clear();
        _mapsCompleted = 0;
        _mapsExpected = 0;
        _chaosUsedOnTarget = 0;
        _quantity = -1;
        Status = "idle";
    }

    private Result Fail(string reason)
    {
        _step = Step.Failed;
        Status = reason;
        return Result.Failed;
    }

    private TargetSnapshot CurrentTarget(GameSnapshot snapshot)
    {
        if (_kind == TargetKind.GuardianMaps)
        {
            var item = FindMapAtSlot(snapshot.Inventory, _targetSlot);
            return item.ItemEntity == 0 || item.Rect is not { } rect
                ? default
                : new(item.ElementAddress, rect, Fingerprint(item));
        }
        var invitation = snapshot.MapReceptacle.Item();
        return invitation is not { Rect: { } invitationRect }
            ? default
            : new(invitation.Value.Element, invitationRect, Fingerprint(invitation.Value));
    }

    private static InventoryView.Item FindMapAtSlot(InventoryView inventory, ElementGeometry.Rect slot)
        => inventory.Items.FirstOrDefault(x =>
            x.Path.Equals(GuardianMapPath, StringComparison.OrdinalIgnoreCase)
            && x.Rect is { } rect
            && Math.Abs(rect.CenterX - slot.CenterX) < 2
            && Math.Abs(rect.CenterY - slot.CenterY) < 2);

    private static string Fingerprint(InventoryView.Item item)
        => $"{item.ItemEntity:X}:{item.Rarity}:{item.Identified}:"
            + string.Join(',', (item.Stats ?? []).Select(x => $"{x.Id}:{x.Value}"));

    private static string Fingerprint(MapReceptacleView.StagedItem item)
        => $"{item.ItemEntity:X}:{item.Rarity}:"
            + string.Join(',', item.Stats.Select(x => $"{x.Id}:{x.Value}"));

    private static int CurrencyCount(InventoryView inventory, string path)
        => inventory.Items.Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Sum(x => Math.Max(1, x.StackSize));

    private static bool HoverOwns(GameSnapshot snapshot, nint hover, nint target)
    {
        for (var depth = 0; depth < 24 && hover != 0; depth++)
        {
            if (hover == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(hover + KnownOffsets.Element.Parent, out var parent)
                || parent == hover) break;
            hover = parent;
        }
        return false;
    }

    private static string SlotKey(ElementGeometry.Rect rect) => $"{rect.CenterX:F1}:{rect.CenterY:F1}";
    private readonly record struct TargetSnapshot(nint Element, ElementGeometry.Rect Rect, string Fingerprint);
}
