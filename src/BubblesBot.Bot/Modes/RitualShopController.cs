using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Diagnostics;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Verified Ritual reward spending. Valuable offers are bought before a reroll; filler is
/// deferred until no reroll remains. Deferral is deliberately out of scope.
/// </summary>
public sealed class RitualShopController
{
    private const int VK_LCONTROL = 0xA2;
    private static readonly TimeSpan HoverSettle = TimeSpan.FromMilliseconds(450);
    /// <summary>Mandatory pause after the Favours window becomes visible before ANY action —
    /// the offer grid populates asynchronously, and acting on an empty snapshot rerolled a
    /// window whose items hadn't rendered yet (live report 2026-07-15).</summary>
    private static readonly TimeSpan OpenSettle = TimeSpan.FromMilliseconds(1500);
    /// <summary>Extra patience for an offer list that is still empty after the settle. Only
    /// after this do we treat "no offers" as real (everything bought) and close.</summary>
    private static readonly TimeSpan EmptyOffersGrace = TimeSpan.FromMilliseconds(4000);

    private enum PendingKind { None, Purchase, Reroll, Close }

    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly HashSet<nint> _attemptedItems = new();
    private PendingKind _pending;
    private nint _pendingItem;
    private string _pendingName = "";
    private string _pendingReason = "";
    private string _pendingMetadataPath = "";
    private float _pendingValue;
    private int _pendingGemLevel;
    private int _pendingQuality;
    private bool _pendingCorrupted;
    private int _pendingTribute;
    private int _pendingRerolls;
    private string _pendingFingerprint = "";
    private nint _hoverItem;
    private TimeSpan _hoverStartedAt;
    private bool _rerollUnavailable;
    private int _rerollsUsed;
    private int _purchases;
    private int _consecutiveRejects;
    private TimeSpan? _windowVisibleSince;
    private string _loggedFingerprint = "";

    public bool IsDone { get; private set; }
    public bool HasPendingAction => _pending != PendingKind.None;
    public string Status { get; private set; } = "pending";

    public RitualShopController(Func<GameSnapshot?> getSnapshot) => _getSnapshot = getSnapshot;

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var view = ctx.Snapshot.RitualWindow;
        if (_pending != PendingKind.None)
        {
            if (!ctx.Input.IsIdle)
            {
                Status = $"waiting for {_pending.ToString().ToLowerInvariant()} confirmation";
                return BehaviorStatus.Running;
            }
            FinalizePending(view);
            if (IsDone) return BehaviorStatus.Success;
            view = ctx.Snapshot.RitualWindow;
        }

        if (!view.IsVisible)
        {
            _windowVisibleSince = null;
            Status = "waiting for Favours window";
            return BehaviorStatus.Failure;
        }

        // Mandatory populate window: no reads-for-decisions, no clicks, until the window
        // has been continuously visible for OpenSettle.
        var now = BotMonotonicClock.Now;
        _windowVisibleSince ??= now;
        var visibleFor = now - _windowVisibleSince.Value;
        if (visibleFor < OpenSettle)
        {
            Status = $"letting Favours populate ({visibleFor.TotalMilliseconds:F0}ms)";
            return BehaviorStatus.Running;
        }

        var all = EvaluateOffers(view, ctx);
        LogOffersOnce(view, all);

        // An empty offer grid is never a reroll target. Early on it means "not populated
        // yet"; past the grace it means everything was bought — close and be done.
        if (all.Count == 0)
        {
            if (visibleFor < EmptyOffersGrace)
            {
                Status = "offer grid still empty; waiting";
                return BehaviorStatus.Running;
            }
            return BeginClose(ctx, view);
        }

        // Remaining tribute can't afford anything once several buys in a row bounce —
        // walking the rest of the filler list at ~2.2s per rejection wasted ~40s per map.
        if (_consecutiveRejects >= 3)
        {
            EventLog.Emit("ritual", "ritual.shop-tribute-spent", EventSeverity.Info,
                $"{_consecutiveRejects} consecutive unaffordable purchases — treating tribute as spent and closing");
            return BeginClose(ctx, view);
        }

        var evaluated = all
            .Where(x => !_attemptedItems.Contains(x.Offer.ItemEntity))
            .OrderByDescending(x => x.TotalChaos)
            .ToArray();

        // The Favours policy comes from the active strategy's Ritual shop block. This controller
        // is only reached when that block is enabled, so it is present here.
        var shop = ctx.Strategy?.Block<Strategies.RitualBlock>()?.Shop ?? new Strategies.RitualShopBlock();
        var canReroll = !_rerollUnavailable
            && _rerollsUsed < shop.MaxRerolls
            && view.RerollsRemaining > 0;
        var candidateIndex = RitualShopPolicy.SelectIndex(
            evaluated.Select((x, index) => new RitualShopCandidate(index, x.TotalChaos, x.IsLiquidFiller)).ToArray(),
            canReroll,
            shop.RerollThresholdChaos,
            shop.FinalBuyMinChaos);
        var candidate = candidateIndex is { } index ? evaluated[index] : default;

        if (candidate.Offer.ItemEntity != 0)
            return TickPurchase(ctx, view, candidate);

        if (canReroll && view.RerollRect is { } rerollRect)
        {
            var best = evaluated.Length > 0 ? evaluated[0].TotalChaos : 0f;
            EventLog.Emit("ritual", "ritual.shop-reroll-decision", EventSeverity.Info,
                $"no offer ≥ {shop.RerollThresholdChaos:F1}c " +
                $"(best {best:F1}c of {evaluated.Length}); rerolling ({_rerollsUsed + 1}/{shop.MaxRerolls})");
            return BeginReroll(ctx, view, rerollRect);
        }

        return BeginClose(ctx, view);
    }

    /// <summary>One offer-table log per distinct offer set — the audit trail for verifying
    /// buy-vs-reroll decisions against what was actually on screen.</summary>
    private void LogOffersOnce(RitualWindowView view, IReadOnlyList<EvaluatedOffer> offers)
    {
        var fingerprint = Fingerprint(view);
        if (fingerprint == _loggedFingerprint) return;
        _loggedFingerprint = fingerprint;
        var lines = offers.Select(x =>
            $"'{x.Name}' x{Math.Max(1, x.Offer.StackSize)} = {x.TotalChaos:F1}c" +
            $"{(x.IsLiquidFiller ? " [filler]" : "")} ({x.Reason})");
        EventLog.Emit("ritual", "ritual.shop-offers", EventSeverity.Info,
            $"Favours populated: {offers.Count} offers, tribute={view.Tribute}, " +
            $"rerolls={view.RerollsRemaining} | " + string.Join(" | ", lines));
    }

    public void Reset()
    {
        _attemptedItems.Clear();
        _pending = PendingKind.None;
        _pendingItem = 0;
        _pendingName = "";
        _pendingReason = "";
        _pendingMetadataPath = "";
        _pendingValue = 0;
        _pendingGemLevel = 0;
        _pendingQuality = 0;
        _pendingCorrupted = false;
        _pendingTribute = 0;
        _pendingRerolls = 0;
        _pendingFingerprint = "";
        _hoverItem = 0;
        _hoverStartedAt = TimeSpan.Zero;
        _rerollUnavailable = false;
        _rerollsUsed = 0;
        _purchases = 0;
        _consecutiveRejects = 0;
        _windowVisibleSince = null;
        _loggedFingerprint = "";
        IsDone = false;
        Status = "pending";
    }

    private BehaviorStatus TickPurchase(
        BehaviorContext ctx, RitualWindowView view, EvaluatedOffer candidate)
    {
        var (sx, sy) = ctx.Snapshot.Window.ToScreen(
            (int)candidate.Offer.Rect.CenterX, (int)candidate.Offer.Rect.CenterY);
        if (_hoverItem != candidate.Offer.ItemEntity)
        {
            _hoverItem = candidate.Offer.ItemEntity;
            _hoverStartedAt = BotMonotonicClock.Now;
            ctx.Input.HoverAt(sx, sy, CursorPriority.BlinkAim);
            Status = $"hovering {candidate.Name} ({candidate.TotalChaos:F1}c)";
            return BehaviorStatus.Running;
        }
        ctx.Input.HoverAt(sx, sy, CursorPriority.BlinkAim);
        if (BotMonotonicClock.Now - _hoverStartedAt < HoverSettle)
            return BehaviorStatus.Running;

        var item = candidate.Offer.ItemEntity;
        var tribute = view.Tribute;
        var ticket = ctx.Input.ModifierClick(sx, sy, [VK_LCONTROL], ClickIntent.InteractUi,
            $"buy Ritual reward {candidate.Name}",
            expectResolved: () =>
            {
                var fresh = _getSnapshot()?.RitualWindow;
                return fresh is { IsVisible: true }
                    && fresh.Tribute < tribute
                    && fresh.Offers.All(x => x.ItemEntity != item);
            },
            timeoutMs: 1600);
        if (!ticket.Accepted) return BehaviorStatus.Running;

        _attemptedItems.Add(item);
        _pending = PendingKind.Purchase;
        _pendingItem = item;
        _pendingName = candidate.Name;
        _pendingReason = candidate.Reason;
        _pendingMetadataPath = candidate.Offer.MetadataPath;
        _pendingValue = candidate.TotalChaos;
        _pendingGemLevel = candidate.Offer.GemLevel;
        _pendingQuality = candidate.Offer.Quality;
        _pendingCorrupted = candidate.Offer.Corrupted;
        _pendingTribute = tribute;
        _hoverItem = 0;
        Status = $"buying {candidate.Name} ({candidate.TotalChaos:F1}c)";
        return BehaviorStatus.Running;
    }

    private BehaviorStatus BeginReroll(
        BehaviorContext ctx, RitualWindowView view, ElementGeometry.Rect rect)
    {
        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var before = Fingerprint(view);
        var tribute = view.Tribute;
        var rerolls = view.RerollsRemaining;
        var ticket = ctx.Input.Click(sx, sy, ClickIntent.InteractUi, "reroll Ritual Favours",
            expectResolved: () =>
            {
                var fresh = _getSnapshot()?.RitualWindow;
                return fresh is { IsVisible: true }
                    && fresh.RerollsRemaining < rerolls
                    && fresh.Tribute < tribute
                    && Fingerprint(fresh) != before;
            },
            timeoutMs: 1800);
        if (!ticket.Accepted) return BehaviorStatus.Running;

        _pending = PendingKind.Reroll;
        _pendingTribute = tribute;
        _pendingRerolls = rerolls;
        _pendingFingerprint = before;
        _hoverItem = 0;
        Status = "rerolling low-value Favours";
        return BehaviorStatus.Running;
    }

    private BehaviorStatus BeginClose(BehaviorContext ctx, RitualWindowView view)
    {
        if (view.CloseRect is not { } rect)
        {
            Status = "Favours close button unavailable";
            return BehaviorStatus.Running;
        }
        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.Click(sx, sy, ClickIntent.InteractUi, "close Ritual Favours",
            expectResolved: () => _getSnapshot()?.RitualWindow.IsVisible == false,
            timeoutMs: 1500);
        if (!ticket.Accepted) return BehaviorStatus.Running;
        _pending = PendingKind.Close;
        _pendingTribute = view.Tribute;
        Status = "closing Favours";
        return BehaviorStatus.Running;
    }

    private void FinalizePending(RitualWindowView view)
    {
        switch (_pending)
        {
            case PendingKind.Purchase:
            {
                var success = view.IsVisible
                    && view.Tribute < _pendingTribute
                    && view.Offers.All(x => x.ItemEntity != _pendingItem);
                if (success)
                {
                    _purchases++;
                    _consecutiveRejects = 0;
                    EventLog.Emit("ritual", "ritual.shop-purchase-confirmed", EventSeverity.Info,
                        $"bought {_pendingName} ({_pendingValue:F1}c) for {_pendingTribute - view.Tribute} tribute",
                        new Dictionary<string, object?>
                        {
                            ["item"] = _pendingName,
                            ["chaosValue"] = _pendingValue,
                            ["valuationReason"] = _pendingReason,
                            ["metadataPath"] = _pendingMetadataPath,
                            ["gemLevel"] = _pendingGemLevel,
                            ["quality"] = _pendingQuality,
                            ["corrupted"] = _pendingCorrupted,
                            ["tributeCost"] = _pendingTribute - view.Tribute,
                            ["tributeRemaining"] = view.Tribute,
                        });
                }
                else
                {
                    _consecutiveRejects++;
                    EventLog.Emit("ritual", "ritual.shop-purchase-rejected", EventSeverity.Warning,
                        $"Ritual purchase produced no state change for {_pendingName}; treating it as unaffordable ({_consecutiveRejects} in a row)");
                }
                break;
            }
            case PendingKind.Reroll:
            {
                var success = view.IsVisible
                    && view.RerollsRemaining < _pendingRerolls
                    && view.Tribute < _pendingTribute
                    && Fingerprint(view) != _pendingFingerprint;
                if (success)
                {
                    _rerollsUsed++;
                    _attemptedItems.Clear();
                    EventLog.Emit("ritual", "ritual.shop-reroll-confirmed", EventSeverity.Info,
                        $"rerolled Favours for {_pendingTribute - view.Tribute} tribute; {view.RerollsRemaining} remain");
                }
                else
                {
                    _rerollUnavailable = true;
                    EventLog.Emit("ritual", "ritual.shop-reroll-rejected", EventSeverity.Warning,
                        "Ritual reroll produced no state change; spending final tribute without retrying it");
                }
                break;
            }
            case PendingKind.Close:
                if (!view.IsVisible)
                {
                    IsDone = true;
                    EventLog.Emit("ritual", "ritual.shop-completed", EventSeverity.Info,
                        $"Ritual shop complete: {_purchases} purchases, {_rerollsUsed} rerolls, {_pendingTribute} tribute left");
                }
                break;
        }
        _pending = PendingKind.None;
    }

    private static IReadOnlyList<EvaluatedOffer> EvaluateOffers(
        RitualWindowView view, BehaviorContext ctx)
    {
        var filter = LootClosestVisible.SharedValueFilter;
        if (filter is null) return Array.Empty<EvaluatedOffer>();
        var result = new List<EvaluatedOffer>(view.Offers.Count);
        foreach (var offer in view.Offers)
        {
            // Shop uniques are identified, but their unique display name is tooltip-only.
            // Treat them as art-resolved unidentified uniques so the existing conservative
            // shared-art pricing logic is reused instead of pricing the base type.
            var facts = new LootItemFacts(
                offer.BaseName,
                offer.BaseName,
                offer.MetadataPath,
                offer.Rarity,
                offer.Rarity == EntityListReader.EntityRarity.Unique ? false : offer.Identified,
                offer.ItemLevel,
                offer.Quality,
                offer.GemLevel,
                offer.Corrupted,
                offer.InventorySlots,
                offer.ResourcePath,
                ClusterPassiveCount: offer.ClusterPassiveCount);
            var evaluation = filter.Evaluate(facts, ctx.Settings.Loot);
            var unit = Math.Max(evaluation.ChaosValue, 0f);
            var total = unit * Math.Max(1, offer.StackSize);
            result.Add(new EvaluatedOffer(offer, offer.BaseName, total, evaluation.Reason,
                RitualShopPolicy.IsLiquidFillerPath(offer.MetadataPath)));
        }
        return result;
    }

    private static string Fingerprint(RitualWindowView view)
        => string.Join(',', view.Offers.Select(x => $"{(long)x.ItemEntity:X}"));

    private readonly record struct EvaluatedOffer(
        RitualWindowView.Offer Offer, string Name, float TotalChaos, string Reason,
        bool IsLiquidFiller);
}
