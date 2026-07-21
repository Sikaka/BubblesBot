using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Combat;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Ultimatum farm mode (first end-to-end deterministic farm loop). The smallest in-map
/// activity that's worth doing for free — a single mob spawner + wave + choice loop with a
/// well-defined exit condition.
///
/// <code>
/// Selector "ultimatum"
///   ├── If(in choice panel) → pick mod + confirm OR take reward
///   ├── If(spawner not started AND ready) → walk + click ground label
///   ├── If(spawner active) → orbit altar + drive-by combat
///   ├── If(loot in range) → halt + LootClosestVisible
///   └── Exit via portal (EnterAreaTransition picks the closest portal)
/// </code>
///
/// <para><b>State tracking.</b> The mode counts rounds by watching the
/// <see cref="UltimatumPanelView.IsVisible"/> edge (false → true = new round prompt). When
/// <see cref="UltimatumSettings.MaxWaves"/> is hit OR cumulative danger crosses
/// <see cref="UltimatumSettings.DangerThreshold"/>, it clicks "take reward" instead of
/// confirming another mod.</para>
///
/// <para><b>UI path discovery — REQUIRES IN-GAME VALIDATION.</b> The exact child indices
/// of choice buttons / confirm / take-reward on the UltimatumPanel aren't committed yet.
/// The mode reads them via <see cref="UltimatumPanelView.DescendantRect"/> with placeholder
/// paths inspired by AutoExile's reads; the first live Ultimatum encounter the bot sees,
/// the user adjusts the index constants here. Logged as INFO events on each panel
/// interaction so the dashboard surfaces what's being clicked.</para>
///
/// <para><b>Out of scope (v1).</b> Hideout-to-map device flow, portal scroll usage,
/// mod-text reading (needed for proper danger scoring), stand-in-circle abandon path,
/// Trial-of-Glory CaptureRune chase. v1 ignores mod text and picks choice 0 every time —
/// "deterministic but dumb." Real danger scoring lights up once UI text reads are wired.</para>
/// </summary>
public sealed class UltimatumMode : IBotMode
{
    // In-encounter panel state machine. Verifies via END STATE (panel closes) rather than
    // SelectedChoice — the in-encounter panel's SelectedChoice field is unreliable across
    // rounds (carries stale values from previous wave + server timing variance). Sequence:
    //
    //   Decide → ClickMod (instant) → ShortDelay (300ms) → ClickAccept → AwaitWaveStart
    //
    // If the panel doesn't close within BeginVerifyTimeoutMs, the WHOLE sequence retries
    // from ClickMod. After MaxClickAttempts failures, fall back to take-reward.
    private enum InEncounterStage { Decide, ClickMod, DelayBeforeAccept, ClickAccept, AwaitWaveStart, ClickTakeReward, AwaitPanelClose }
    private InEncounterStage _inEncounterStage = InEncounterStage.Decide;
    private DateTime _lastInEncounterClickAt = DateTime.MinValue;
    private DateTime _waveSequenceStartedAt = DateTime.MinValue;
    private int _waveSequenceAttempts;
    private int _inEncounterTakeClickAttempts;
    private int _inEncounterChosenIndex;
    private string _inEncounterChosenId = string.Empty;
    // Brief cursor-settle window between the mod-click and the accept-trial click — just
    // enough for the cursor to physically reach the accept button + UI focus to land on it
    // before the click event fires. Per the interaction golden rule: 20-50ms is the floor;
    // 500ms plus InputRouter.IsIdle gives the full pointer transaction time to settle so
    // accept-trial sees the panel in "mod selected, can confirm" state. We poll for the panel-close
    // signal every tick from that point — there is NO additional fixed wait beyond this.
    private const int ModToAcceptDelayMs = 500;

    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly Func<FarmingStrategy?> _getStrategy;
    private readonly SettingsStore _settings;
    private readonly bool _exitWhenDone;

    private readonly MovementSystem _movement;
    private readonly CombatSystem   _combat = new();
    private readonly FlaskSystem    _flasks = new();
    private readonly InteractSystem _interact = new();
    private readonly SkillBook      _skills = new();
    private readonly LootClosestVisible _loot;
    private readonly IBehavior _root;

    // Encounter bookkeeping.
    private uint     _spawnerEntityId;
    private Core.Game.Vector2i? _spawnerAnchor;
    /// <summary>True once we've decided to abandon (Trial-of-Glory variant detected).
    /// The mode shifts straight to portal exit when set.</summary>
    private bool _abandonRequested;

    /// <summary>
    /// Pre-start state machine with click-verify semantics:
    /// <list type="number">
    ///   <item><b>ReadType</b> — settle, read encounter type + mods, pick a mod.</item>
    ///   <item><b>ClickMod</b> — fire one mod-click; advance to AwaitModSelected.</item>
    ///   <item><b>AwaitModSelected</b> — poll <see cref="UltimatumPreStartLabel.SelectedChoice"/>
    ///         until it matches the chosen index. Retry click on timeout; abandon after
    ///         <see cref="MaxClickAttempts"/> failures.</item>
    ///   <item><b>ClickBegin</b> — fire one BEGIN-click; advance to AwaitEncounterStart.</item>
    ///   <item><b>AwaitEncounterStart</b> — poll for the encounter-started signal (spawner
    ///         non-targetable OR ground label gone). Retry BEGIN on timeout; abandon after
    ///         <see cref="MaxClickAttempts"/>.</item>
    /// </list>
    /// All clicks are throttled to <see cref="ClickThrottleMs"/> between attempts and verified
    /// before advancing, per the CLAUDE.md "never assume a click succeeded" rule.
    /// </summary>
    private enum PreStartStage { ReadType, ClickMod, AwaitModSelected, ClickBegin, AwaitEncounterStart }
    private PreStartStage _preStartStage = PreStartStage.ReadType;
    private DateTime _lastPreStartClickAt = DateTime.MinValue;
    private int _modClickAttempts;
    private int _beginClickAttempts;
    private bool _beginEverDispatched;
    private const int MaxClickAttempts = 5;
    /// <summary>Minimum gap between successive clicks of THE SAME button. Just enough to
    /// let the cursor reach the target + UI focus the element before the click event fires.
    /// Per the interaction golden rule: 20-50ms settle is the floor; 150ms gives a tiny
    /// buffer for input-router queueing. Polling for confirmation happens every render tick
    /// (~7ms) independently of this — this throttle only gates RE-CLICKS, not verifies.</summary>
    private const int ClickThrottleMs  = 150;
    /// <summary>Max ms to wait for a click's confirmation signal before re-clicking. Polled
    /// every render tick (~7ms) the whole way — first success short-circuits.</summary>
    private const int VerifyTimeoutMs  = 800;
    /// <summary>Same idea, but BEGIN/accept-trial closes a panel (heavier game-side state
    /// transition) so the timeout is a bit longer before we re-click.</summary>
    private const int BeginVerifyTimeoutMs = 1200;
    private DateTime _lastVerifyLogAt = DateTime.MinValue;

    /// <summary>Picked mod index + id, latched on transition out of <see cref="PreStartStage.ReadType"/>.
    /// The id is added to <see cref="_cumulativeDanger"/> when the BEGIN click fires.</summary>
    private int _chosenModIndex;
    private string _chosenModId = string.Empty;
    private int _chosenModDanger;
    private int _inEncounterChosenDanger;

    // ── Dynamic arena radius ─────────────────────────────────────────────
    /// <summary>Live arena radius the bot orbits within. Starts at the user-configured
    /// <see cref="UltimatumSettings.OrbitRadius"/>; halves when the chieftain picks the
    /// <c>Radius1</c> ("Limited Arena") mod. May also tighten when we observe a confirmed
    /// out-of-bounds sample at a distance less than the current radius — see
    /// <see cref="_minOutsideDistance"/>. Never widens automatically.</summary>
    private float _currentArenaRadius;
    /// <summary>Maximum player-to-spawner distance ever observed while the boundary
    /// warning was <em>hidden</em> (player inside). Lower bound on the true radius. Used
    /// for diagnostics — even a max-inside doesn't prove the bot is "safe out to N", it
    /// just proves the bot has been at N without the warning firing. Recorded continuously.</summary>
    private float _maxInsideDistance;
    /// <summary>Minimum player-to-spawner distance observed while the boundary warning
    /// was <em>visible</em> (player outside). Strict upper bound on the true radius —
    /// once observed, the bot tightens <see cref="_currentArenaRadius"/> to stay inside
    /// this minus a safety margin. Initialized to +inf.</summary>
    private float _minOutsideDistance = float.PositiveInfinity;
    /// <summary>Pull-back-to-spawner safety margin (grid units) applied below
    /// <see cref="_minOutsideDistance"/> when calibrating the dynamic radius. Set small
    /// enough that the bot isn't forced into the center, large enough that float jitter
    /// and a half-step of player movement don't put us out of bounds.</summary>
    private const float BoundarySafetyMargin = 4f;

    // ── Settle gate ──────────────────────────────────────────────────────
    /// <summary>Wall-clock the first tick we observed the pre-start label's modifier list
    /// populated. The bot waits ~500 ms after this before clicking, because:
    /// <list type="bullet">
    ///   <item>The ground label only populates after the player arrives near the spawner —
    ///         spawner-in-cache fires earlier (entity streams in from further out).</item>
    ///   <item>Even once populated, PoE briefly re-tweens child geometry; clicking on the
    ///         first populated tick lands on rects that are still in motion.</item>
    /// </list>
    /// Set to <see cref="DateTime.MinValue"/> when we don't yet have a populated panel.</summary>
    private DateTime _modsFirstPopulatedAt = DateTime.MinValue;

    // ── BEGIN verify + retry ─────────────────────────────────────────────
    /// <summary>Wall-clock the BEGIN click last fired. If <see cref="_encounterStarted"/>
    /// hasn't flipped within a few seconds, we reset to <see cref="PreStartStage.ReadType"/>
    /// and re-attempt. Cumulative danger is added on the encounter-started edge, NOT on
    /// the click itself, so retries don't double-count.</summary>
    private DateTime _lastBeginClickAt = DateTime.MinValue;

    // ── Boundary-discovery diagnostic ────────────────────────────────────
    /// <summary>Wall-clock when the encounter-start diagnostic stops recording. Set on
    /// the encounter-started edge to <c>now + 10s</c>. While active, the mode logs any
    /// newly-appeared entities + player buffs — hunting for the arena-boundary entity
    /// or buff the earlier research couldn't find.</summary>
    private DateTime _diagnosticEndsAt = DateTime.MinValue;
    private readonly HashSet<uint>   _preEncounterEntityIds = new();
    private readonly HashSet<string> _preEncounterBuffNames = new(StringComparer.Ordinal);
    /// <summary>Entity IDs we've already logged during the discovery window — prevents
    /// spamming the events panel when the same entity sticks around for multiple ticks.</summary>
    private readonly HashSet<uint> _diagnosticLoggedEntities = new();
    private bool     _encounterStarted;           // true once ground label click confirmed
    private int      _roundsCompleted;            // 0 after BEGIN; advances when the post-wave panel opens
    private int      _cumulativeDanger;
    private bool     _panelWasVisible;            // edge detector for round transitions
    private bool     _encounterDone;              // verified terminal state → settle, loot, exit
    private uint     _spawnerAreaHash;
    private uint     _lastAreaHash;
    private bool     _bossArena;
    private bool     _bossHostileSeen;
    private TimeSpan? _bossQuietSince;
    private DateTime _spawnerObservedAt = DateTime.MinValue;
    private DateTime _waveStartedAt = DateTime.MinValue;
    private DateTime _panelOpenedAt = DateTime.MinValue;
    private DateTime _encounterCompletedAt = DateTime.MinValue;
    private const int PreStartTimeoutSeconds = 60;
    private const int ChoicePanelTimeoutSeconds = 30;
    private const int WaveTimeoutSeconds = 150;
    /// <summary>PoE materializes accumulated rewards asynchronously after the terminal state.
    /// Do not inspect the pile or allow map exit until this full grace period has elapsed.</summary>
    private const double RewardSettlementSeconds = 5;

    public string Name => "Ultimatum";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";

    public bool IsFighting => _encounterStarted && !_encounterDone && !_abandonRequested;
    public bool IsBossArena => _bossArena;
    /// <summary>Portal-safe terminal state. Pre-start skips may leave; after BEGIN has been
    /// dispatched, only observed encounter completion may leave the map.</summary>
    public bool IsTerminal => _encounterDone
        || (_abandonRequested && !_encounterStarted && !_beginEverDispatched);

    public bool WantsControl(BehaviorContext ctx)
    {
        if (ctx.Strategy?.Block<UltimatumBlock>() is not { Enabled: true } block) return false;
        if (IsTerminal) return block.ExitAfter;
        if (_spawnerEntityId != 0 || _encounterStarted || _bossArena) return true;
        if (ctx.Entities is not null && ctx.Snapshot.Ultimatum(ctx.Entities).Spawner is not null) return true;
        return ctx.Snapshot.TileMap.Has("ultimatum_altar");
    }

    public bool ShouldExitMap(BehaviorContext ctx)
        => IsTerminal
        && ctx.Strategy?.Block<UltimatumBlock>() is { Enabled: true, ExitAfter: true }
        && (!_encounterDone
            || IsRewardSettlementComplete);

    private bool IsRewardSettlementComplete
        => _encounterCompletedAt != DateTime.MinValue
            && (DateTime.UtcNow - _encounterCompletedAt).TotalSeconds >= RewardSettlementSeconds;

    private bool IsRewardSettling
        => _encounterDone && !IsRewardSettlementComplete;

    /// <summary>True when Ultimatum must preempt the ordinary mapping tree. The long run to
    /// the altar is deliberately non-exclusive so normal threat handling and loot can fire;
    /// the UI/arena/terminal phases remain exclusive.</summary>
    public bool RequiresExclusiveControl(BehaviorContext ctx)
    {
        if (IsTerminal || _encounterStarted || _bossArena || ctx.Snapshot.UltimatumPanel.IsVisible)
            return true;
        if (_spawnerAnchor is not { } anchor || ctx.Live is not { } live) return false;
        var dx = anchor.X - live.GridPosition.X;
        var dy = anchor.Y - live.GridPosition.Y;
        return dx * dx + dy * dy <= 45 * 45;
    }

    public UltimatumMode(SettingsStore settings, Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive,
        Func<EntityCache?> getEntities, bool exitWhenDone = true,
        Func<FarmingStrategy?>? getStrategy = null)
    {
        _settings    = settings;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        _getStrategy = getStrategy ?? (() => null);
        _exitWhenDone = exitWhenDone;
        _movement    = new MovementSystem(settings);
        _loot        = new LootClosestVisible("loot closest", _interact, getSnapshot);
        // Accumulated Ultimatum rewards are already earned; drain the visible pile without
        // applying the incidental-map value threshold. Occlusion, click verification, and
        // label blacklisting still apply inside LootClosestVisible.
        _loot.BypassValueFilter = true;
        _loot.BypassTerrainLineOfSight = true;
        _loot.IgnoreCompletedUltimatumAnchorOcclusion = true;
        _currentArenaRadius = settings.Current.Ultimatum.OrbitRadius;

        // Approach: walk to the live spawner anchor. v1 has no in-map auto-locator
        // (TileEntities' "CameraZoom/TrialmasterBoss" turned out to be the post-encounter
        // boss arena, not the entry — completely wrong location). The user explores to the
        // encounter; once within the network bubble the spawner entity streams into the
        // cache and we take over. Future: scout via ExplorationSystem when no anchor known.
        var spawnerApproach = new FollowPath("approach spawner", _movement,
            ctx => SpawnerNeedsApproach(ctx) ? _spawnerAnchor : null,
            _skills,
            goalArrivalRadius: 8f);

        // Pre-start handler — reads the spawner's floating ground label, decides whether to
        // engage (skip Trial-of-Glory "stand in circle"), picks a mod by configured index,
        // clicks BEGIN. Replaces the simpler "click spawner label" approach because the
        // actual click target isn't the spawner label itself — it's two specific child
        // buttons (mod-choice + BEGIN) inside the label tree.
        var preStartAction = new Behaviors.Action("ultimatum pre-start", HandlePreStart);

        var attackBranch = new If("attack in range",
            ctx => HasEnemyInRange(ctx),
            new Cast("attack", _combat, ctx => PickAttackSlot(ctx.Settings),
                Aim.AtClosestEnemy(60f), _skills));

        // Orbit walk — keeps the bot within OrbitRadius of the altar during combat. When we're
        // already inside the radius, returns Failure so the parallel falls through to combat.
        var orbitWalk = new FollowPath("orbit", _movement,
            ctx => NeedsToOrbit(ctx) ? _spawnerAnchor : null,
            _skills,
            goalArrivalRadiusProvider: ctx => ctx.Settings.Ultimatum.OrbitRadius * 0.5f);

        _root = new Selector("ultimatum",
            // Choice panel handling. Visible between rounds — pick a mod and confirm, OR take
            // reward if we've hit the threshold. Eats the click cooldown so we don't fire
            // every render tick.
            new If("panel visible",
                ctx => ctx.Snapshot.UltimatumPanel.IsVisible,
                new Behaviors.Action("handle panel", HandleChoicePanel)),

            // Walk to spawner if we haven't started yet AND it exists in cache.
            new If("approach spawner",
                ctx => SpawnerNeedsApproach(ctx),
                spawnerApproach),

            // Pre-start panel handling: read encounter type, pick mod, click BEGIN.
            new If("pre-start panel",
                ctx => SpawnerNeedsClick(ctx),
                preStartAction),

            // Encounter active: combat. Orbit XOR attack — same gate pattern BlightMode uses
            // to avoid drive-by drift (orbit walk holds movement, attack moves cursor → bot
            // walks toward enemy instead of staying anchored).
            new If("encounter active", InEncounter,
                new Behaviors.Action("orbit+combat", ctx =>
                {
                    if (NeedsToOrbit(ctx)) orbitWalk.Tick(ctx);
                    else
                    {
                        _movement.Release();
                        attackBranch.Tick(ctx);
                    }
                    return BehaviorStatus.Running;
                })),

            // Rewards are spawned asynchronously after the altar flips to its terminal state.
            // Hold position for the complete grace period before both loot arbitration and the
            // portal-exit gate, otherwise an empty early snapshot can incorrectly look drained.
            new If("reward settlement", _ => IsRewardSettling,
                new Behaviors.Action("wait for rewards", ctx =>
                {
                    _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
                    return BehaviorStatus.Running;
                })),

            // Pickup what dropped, anywhere in loot range.
            new If("loot in range", HasCollectableLoot,
                new Sequence("loot",
                    new StopMoving("halt loot", _movement),
                    _loot)),

            // Done with the encounter — leave via portal. Fires when:
            //   • encounter ran and finished (_encounterDone), OR
            //   • we decided to abandon pre-start (_abandonRequested, e.g. Trial-of-Glory)
            // Without one of these flags, the mode idles in-place rather than walking to a
            // portal; this keeps the bot from leaking out of the map just because it can't
            // see a spawner yet.
            new If("done or abandoning",
                _ => _exitWhenDone && (_encounterDone || _abandonRequested),
                new EnterAreaTransition("exit", _interact, _movement, _skills, _getSnapshot)));
    }

    public void Reset()
    {
        _movement.Release();
        _combat.StopAllChannels();
        _flasks.Reset();
        _interact.Cancel();
        _skills.Reset();
        _loot.Reset();
        _root.Reset();

        _spawnerEntityId = 0;
        _spawnerAnchor = null;
        _abandonRequested = false;
        _preStartStage = PreStartStage.ReadType;
        _lastPreStartClickAt = DateTime.MinValue;
        _modsFirstPopulatedAt = DateTime.MinValue;
        _lastBeginClickAt = DateTime.MinValue;
        _modClickAttempts = 0;
        _beginClickAttempts = 0;
        _beginEverDispatched = false;
        _lastVerifyLogAt = DateTime.MinValue;
        _chosenModIndex = 0;
        _chosenModId = string.Empty;
        _currentArenaRadius = _settings.Current.Ultimatum.OrbitRadius;
        _maxInsideDistance = 0f;
        _minOutsideDistance = float.PositiveInfinity;
        _diagnosticEndsAt = DateTime.MinValue;
        _preEncounterEntityIds.Clear();
        _preEncounterBuffNames.Clear();
        _diagnosticLoggedEntities.Clear();
        _encounterStarted = false;
        _roundsCompleted = 0;
        _cumulativeDanger = 0;
        _panelWasVisible = false;
        _inEncounterStage = InEncounterStage.Decide;
        _lastInEncounterClickAt = DateTime.MinValue;
        _waveSequenceStartedAt = DateTime.MinValue;
        _waveSequenceAttempts = 0;
        _inEncounterTakeClickAttempts = 0;
        _inEncounterChosenIndex = 0;
        _inEncounterChosenId = string.Empty;
        _chosenModDanger = 0;
        _inEncounterChosenDanger = 0;
        _encounterDone = false;
        _spawnerAreaHash = 0;
        _lastAreaHash = 0;
        _bossArena = false;
        _bossHostileSeen = false;
        _bossQuietSince = null;
        _spawnerObservedAt = DateTime.MinValue;
        _waveStartedAt = DateTime.MinValue;
        _panelOpenedAt = DateTime.MinValue;
        _encounterCompletedAt = DateTime.MinValue;
        LastDecision = "reset";
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } pv) _skills.SetActorContext(pv.ActorComponentAddress);
        if (_skills.CooldownReader is null) _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities(), _getStrategy());

        UpdateAreaAndTileAnchor(ctx);

        // A bot restart/disarm can occur while a completed-wave panel is open. The panel
        // identifies the next offered wave, so Current-1 recovers the completed-wave count.
        if (!_encounterStarted
            && snapshot.UltimatumPanel.IsVisible
            && snapshot.UltimatumPanel.RoundProgress is { Current: >= 2 } recovered)
        {
            _encounterStarted = true;
            _roundsCompleted = Math.Max(_roundsCompleted, recovered.Current - 1);
            _waveStartedAt = DateTime.UtcNow;
            BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                $"recovered encounter with {_roundsCompleted} completed wave(s) from " +
                $"next-wave panel {recovered.Current}/{recovered.Max}");
        }

        // Track spawner across ticks. Survives the spawner falling out of the entity list
        // post-completion (out-of-bubble caches keep it around). _encounterDone latches once
        // the entity is gone or its IsTargetable flag has flipped — that's our universal
        // "encounter complete, time to loot + leave" signal.
        UpdateSpawnerState(ctx);

        // Boundary calibration: each tick, sample player distance from spawner + the "return
        // to area" warning's visibility. Min observed-out-of-bounds distance becomes a strict
        // upper bound on the arena radius; we orbit at min(setting, observed-min - margin).
        // Pure data collection here — the orbit logic reads the running min in NeedsToOrbit.
        SampleBoundary(ctx);

        // Edge-detect the two distinct transitions: panel opening proves the active wave
        // completed; panel closing after Accept Trial proves the selected next wave started.
        var panelVisible = snapshot.UltimatumPanel.IsVisible;
        if (panelVisible && !_panelWasVisible)
        {
            _panelOpenedAt = DateTime.UtcNow;
            // The visible counter identifies the wave offered by Accept Trial. Therefore
            // a 2/10 panel proves wave 1 completed, while the initial BEGIN starts wave 1
            // from a completed count of zero.
            var completedFromPanel = snapshot.UltimatumPanel.RoundProgress is { Current: >= 2 } progress
                ? progress.Current - 1
                : _roundsCompleted + 1;
            if (completedFromPanel > _roundsCompleted)
            {
                _roundsCompleted = completedFromPanel;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"wave {_roundsCompleted} complete (reward-choice panel opened)");
            }
        }
        if (_panelWasVisible && !panelVisible)
        {
            _waveStartedAt = DateTime.UtcNow;
            _panelOpenedAt = DateTime.MinValue;

            // The panel handler is selected only while the panel is visible, so its
            // Await* states cannot observe the very frame that proves a click succeeded.
            // Consume that falling edge here, where every tick passes, and advance the
            // transaction exactly once.
            if (_inEncounterStage == InEncounterStage.AwaitWaveStart)
            {
                _cumulativeDanger += _inEncounterChosenDanger;
                _inEncounterStage = InEncounterStage.Decide;
                _waveSequenceAttempts = 0;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                    $"accept confirmed; wave {_roundsCompleted + 1} started " +
                    $"(panel closed, cumulative danger {_cumulativeDanger})");
            }
            else if (_inEncounterStage == InEncounterStage.AwaitPanelClose)
            {
                MarkEncounterDone("take reward confirmed (panel closed)");
                _inEncounterStage = InEncounterStage.Decide;
            }
        }
        _panelWasVisible = panelVisible;

        ApplyTimeouts(ctx, panelVisible);
        UpdateBossCompletion(ctx);

        _flasks.Tick(ctx);
        var status = _root.Tick(ctx);

        // Boundary-discovery diagnostic. Active for 10s after the encounter starts; logs new
        // entities + new player buffs to help find whatever surface represents the arena ring.
        TickBoundaryDiagnostic(ctx);

        var panel = snapshot.UltimatumPanel.IsVisible ? "PANEL" : "—";
        var ult = snapshot.Ultimatum(_getEntities()!);
        var spawner = ult.Spawner is not null ? $"spawner id={ult.Spawner.Id}" : "no spawner";
        var abandon = _abandonRequested ? " ABANDON" : "";
        var loot = _encounterDone ? $" loot={_loot.LastDecision}" : string.Empty;
        LastDecision = $"status={status} round={_roundsCompleted}/{ctx.Settings.Ultimatum.MaxWaves} danger={_cumulativeDanger} r={_currentArenaRadius:F0} {panel} {spawner}{abandon}{loot}";
    }

    // ── Per-tick state tracking ──────────────────────────────────────────

    private void UpdateAreaAndTileAnchor(BehaviorContext ctx)
    {
        var area = ctx.Snapshot.AreaHash;
        if (area != 0 && _lastAreaHash != 0 && area != _lastAreaHash && _encounterStarted)
        {
            _bossArena = true;
            _spawnerAnchor = ctx.Live?.GridPosition;
            _waveStartedAt = DateTime.UtcNow;
            _bossHostileSeen = false;
            _bossQuietSince = null;
            BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                $"area changed during encounter (0x{_lastAreaHash:X8} -> 0x{area:X8}); treating as Trialmaster arena");
        }
        if (area != 0) _lastAreaHash = area;

        if (_spawnerEntityId != 0 || _spawnerAnchor is not null || _bossArena) return;
        var hits = ctx.Snapshot.TileMap.Find("ultimatum_altar");
        if (hits.Count == 0) return;
        var tile = hits[0];
        _spawnerAnchor = new Core.Game.Vector2i
        {
            X = tile.X + KnownOffsets.TileGridCells / 2,
            Y = tile.Y + KnownOffsets.TileGridCells / 2,
        };
        BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
            $"altar tile found at ({tile.X},{tile.Y}); rushing center ({_spawnerAnchor.Value.X},{_spawnerAnchor.Value.Y})");
    }

    private void ApplyTimeouts(BehaviorContext ctx, bool panelVisible)
    {
        if (IsTerminal) return;
        var now = DateTime.UtcNow;
        if (!_encounterStarted && _spawnerObservedAt != DateTime.MinValue
            && (now - _spawnerObservedAt).TotalSeconds > PreStartTimeoutSeconds)
        {
            // Safe only if BEGIN was never dispatched. Once it was, an unobserved active
            // encounter is possible and opening a portal would instantly fail it.
            if (!_beginEverDispatched)
            {
                _abandonRequested = true;
                _movement.Release();
                BubblesBot.Bot.Diagnostics.EventLog.Emit(
                    "Ultimatum", "ultimatum.timeout", BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
                    $"ABANDON: pre-start exceeded {PreStartTimeoutSeconds}s before BEGIN; leaving map");
            }
            else
            {
                _spawnerObservedAt = now;
                BubblesBot.Bot.Diagnostics.EventLog.Emit(
                    "Ultimatum", "ultimatum.timeout", BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
                    "BEGIN state is uncertain; staying in-map and retrying rather than failing the encounter via portal");
            }
            return;
        }
        else if (panelVisible && _panelOpenedAt != DateTime.MinValue
            && (now - _panelOpenedAt).TotalSeconds > ChoicePanelTimeoutSeconds)
        {
            _inEncounterStage = InEncounterStage.ClickTakeReward;
            _inEncounterTakeClickAttempts = 0;
            _panelOpenedAt = now;
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "Ultimatum", "ultimatum.timeout", BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
                $"choice panel exceeded {ChoicePanelTimeoutSeconds}s; attempting Take Reward in-map");
            return;
        }
        else if (_encounterStarted && !panelVisible && _waveStartedAt != DateTime.MinValue
            && (now - _waveStartedAt).TotalSeconds > WaveTimeoutSeconds)
        {
            _waveStartedAt = now;
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "Ultimatum", "ultimatum.timeout", BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
                $"wave exceeded {WaveTimeoutSeconds}s; continuing combat because portalling would fail the encounter");
        }
    }

    private void UpdateBossCompletion(BehaviorContext ctx)
    {
        if (!_bossArena || IsTerminal || ctx.Entities is null) return;
        var hostile = ctx.Entities.Entries.Values.Any(e => TargetEligibility.IsEligible(e));
        if (hostile)
        {
            _bossHostileSeen = true;
            _bossQuietSince = null;
            return;
        }
        if (!_bossHostileSeen) return;
        _bossQuietSince ??= BotMonotonicClock.Now;
        if ((BotMonotonicClock.Now - _bossQuietSince.Value).TotalSeconds < 4) return;
        MarkEncounterDone("Trialmaster arena hostile-free for 4s after combat");
    }

    private void UpdateSpawnerState(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return;

        var ult = ctx.Snapshot.Ultimatum(ctx.Entities);

        if (ult.Spawner is { } sp)
        {
            if (_spawnerEntityId == 0)
            {
                _spawnerEntityId = sp.Id;
                _spawnerAnchor   = sp.GridPosition;
                _spawnerAreaHash = ctx.Snapshot.AreaHash;
                _spawnerObservedAt = DateTime.UtcNow;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"spawner found id={sp.Id} at ({sp.GridPosition.X},{sp.GridPosition.Y})");
            }
            var altarStarted = StateMachineView.ReadValue(
                ctx.Snapshot.Reader, sp.StateMachineCompAddr,
                UltimatumStates.Spawner.LiveEncounterStarted) == 1;
            var altarFinished = StateMachineView.ReadValue(
                ctx.Snapshot.Reader, sp.StateMachineCompAddr,
                UltimatumStates.Spawner.LiveEncounterFinished) == 1;
            if (altarFinished)
            {
                MarkEncounterDone("altar encounter_finished state = 1");
                return;
            }
            // Start only from the named StateMachine contract. Targetability is not a
            // lifecycle signal: a freshly streamed altar may transiently be non-targetable.
            if (altarStarted && !_encounterStarted)
            {
                _encounterStarted = true;
                _waveStartedAt = DateTime.UtcNow;
                _cumulativeDanger += _chosenModDanger;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"wave 1 started (0 completed) — added danger {_chosenModDanger} for " +
                    $"{_chosenModId} (cumulative {_cumulativeDanger})");

                // Boundary-discovery diagnostic. We freeze the current entity-id + player-buff
                // sets, then for the next 10s log anything new — hunting for the arena ring
                // entity or buff that earlier research couldn't locate.
                StartBoundaryDiagnostic(ctx);

                // Reset the pre-start stage + attempt counters for the next round's
                // between-wave panel handling.
                _preStartStage = PreStartStage.ReadType;
                _lastBeginClickAt = DateTime.MinValue;
                _modClickAttempts = 0;
                _beginClickAttempts = 0;
                _modsFirstPopulatedAt = DateTime.MinValue;
            }
        }
        // A missing cache entry is not completion evidence. It can disappear transiently
        // while streaming or refreshing; remain in-map until encounter_finished is observed.
    }

    // ── Behavior predicates ──────────────────────────────────────────────

    private bool SpawnerNeedsApproach(BehaviorContext ctx)
    {
        if (_encounterDone || _encounterStarted || _abandonRequested) return false;
        var anchor = _spawnerAnchor;
        if (anchor is null) return false;
        if (ctx.Live is not { } live) return false;
        var dx = anchor.Value.X - live.GridPosition.X;
        var dy = anchor.Value.Y - live.GridPosition.Y;
        return dx * dx + dy * dy > 12 * 12;
    }

    private bool SpawnerNeedsClick(BehaviorContext ctx)
    {
        if (_encounterDone || _encounterStarted || _abandonRequested) return false;
        if (ctx.Entities is null) return false;
        var ult = ctx.Snapshot.Ultimatum(ctx.Entities);
        return ult.HasActiveSpawner;
    }

    private bool InEncounter(BehaviorContext ctx)
        => _encounterStarted && !_encounterDone && !ctx.Snapshot.UltimatumPanel.IsVisible;

    private bool NeedsToOrbit(BehaviorContext ctx)
    {
        if (_spawnerAnchor is null) return false;
        if (ctx.Live is not { } live) return false;
        // Hard force-pull when the game tells us we're out of bounds — overrides everything.
        if (ctx.Snapshot.UltimatumBoundary.IsOutsideArena) return true;
        // Effective radius: tightened by the minimum out-of-bounds distance we've ever
        // observed (with a safety margin). If we've never been outside, fall back to the
        // user-configured static OrbitRadius. The Radius1 mod halves _currentArenaRadius
        // separately on pick.
        var r = _currentArenaRadius > 0 ? _currentArenaRadius : ctx.Settings.Ultimatum.OrbitRadius;
        if (!float.IsPositiveInfinity(_minOutsideDistance))
        {
            var bounded = _minOutsideDistance - BoundarySafetyMargin;
            if (bounded < r) r = bounded;
        }
        var dx = _spawnerAnchor.Value.X - live.GridPosition.X;
        var dy = _spawnerAnchor.Value.Y - live.GridPosition.Y;
        return dx * dx + dy * dy > r * r;
    }

    /// <summary>
    /// Per-tick boundary sample. While the "return to area" warning is visible, the player
    /// is strictly out of bounds — record min(observed-distance) as an upper bound on the
    /// true arena radius. While hidden, the player is inside — record max(observed-distance)
    /// for diagnostics (it's a strictly weaker lower bound). NeedsToOrbit consumes the min
    /// to keep the bot inside the safe zone.
    /// </summary>
    private void SampleBoundary(BehaviorContext ctx)
    {
        if (_spawnerAnchor is null) return;
        if (ctx.Live is not { } live) return;
        var dx = _spawnerAnchor.Value.X - live.GridPosition.X;
        var dy = _spawnerAnchor.Value.Y - live.GridPosition.Y;
        var dist = MathF.Sqrt(dx * dx + dy * dy);

        var outside = ctx.Snapshot.UltimatumBoundary.IsOutsideArena;
        if (outside)
        {
            if (dist < _minOutsideDistance)
            {
                _minOutsideDistance = dist;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/boundary",
                    $"new MIN outside distance: {dist:F1} (upper bound on radius)");
            }
        }
        else
        {
            if (dist > _maxInsideDistance)
            {
                _maxInsideDistance = dist;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/boundary",
                    $"new MAX inside distance: {dist:F1}");
            }
        }
    }

    private static bool HasEnemyInRange(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return false;
        var p = ctx.Live.Value.GridPosition;
        const float range = 50f;
        var range2 = range * range;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(e)) continue;
            float dx = e.GridPosition.X - p.X, dy = e.GridPosition.Y - p.Y;
            if (dx * dx + dy * dy <= range2) return true;
        }
        return false;
    }

    public bool HasCollectableLoot(BehaviorContext ctx)
    {
        var r = ctx.Settings.LootRangeGrid;
        var r2 = r * r;
        foreach (var label in ctx.Snapshot.GroundLabels)
        {
            if (!label.IsItem || !label.IsLabelVisible) continue;
            if (_loot.IsBlacklistedLabel(label.LabelAddress)) continue;
            if (label.EntityGridPosition is { } spot && _loot.IsSpotAbandoned(spot)) continue;
            var d = label.DistanceToPlayer;
            if (float.IsInfinity(d)) continue;
            if (d * d <= r2) return true;
        }
        return false;
    }

    // ── Panel interaction ──────────────────────────────────────────────

    private static int Danger(UltimatumModifierChoice choice, IReadOnlyDictionary<string, int> overrides)
        => UltimatumModDanger.GetDanger(choice.Id, choice.Name, choice.Description, overrides);

    private static int PickBest(IReadOnlyList<UltimatumModifierChoice> choices,
        IReadOnlyDictionary<string, int> overrides)
    {
        var bestIndex = -1;
        var bestDanger = int.MaxValue;
        for (var i = 0; i < choices.Count; i++)
        {
            var danger = Danger(choices[i], overrides);
            if (danger >= UltimatumModDanger.BlockedValue || danger >= bestDanger) continue;
            bestDanger = danger;
            bestIndex = i;
        }
        return bestIndex;
    }

    private static string FirstLine(string value)
        => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;

    private static bool EncounterTypeAllowed(string type, UltimatumBlock? block, bool abandonCircles)
    {
        var first = FirstLine(type);
        if (first.Contains("Stone Circle", StringComparison.OrdinalIgnoreCase))
            return !abandonCircles && (block?.DoStandInCircles ?? true);
        if (block is null) return true;
        if (first.Contains("Survive", StringComparison.OrdinalIgnoreCase)) return block.DoSurvive;
        if (first.Contains("Defend", StringComparison.OrdinalIgnoreCase)
            || first.Contains("Protect", StringComparison.OrdinalIgnoreCase)) return block.DoDefendAltar;
        if (first.Contains("Kill", StringComparison.OrdinalIgnoreCase)
            || first.Contains("Defeat", StringComparison.OrdinalIgnoreCase)) return block.DoKillEnemies;
        return false;
    }

    /// <summary>
    /// Pre-start ground-label handler. See <see cref="PreStartStage"/> for the full state
    /// machine — read → click mod → poll selected → click BEGIN → poll encounter-started.
    /// Each click stage owns its own throttle so the verify stages can poll freely; no
    /// top-level throttle starves the polling loops.
    /// </summary>
    private BehaviorStatus HandlePreStart(BehaviorContext ctx)
    {
        var snap = ctx.Snapshot;
        if (ctx.Entities is null) return BehaviorStatus.Failure;
        var ult = snap.Ultimatum(ctx.Entities);
        if (ult.Spawner is not { } spawner) return BehaviorStatus.Failure;

        // Find the spawner's ground label. PoE renders the choice panel as a child of the
        // entity's hover label, not a top-level UI element.
        GroundLabelView? lbl = null;
        foreach (var l in snap.GroundLabels)
        {
            if (l.ItemEntityAddress == spawner.Address) { lbl = l; break; }
        }
        if (lbl is null)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum", "no ground label for spawner — waiting");
            return BehaviorStatus.Running;
        }

        var preStart = UltimatumPreStartLabel.FromGroundLabel(snap.Reader, lbl.LabelElementAddress);
        if (!preStart.IsValid)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum", "pre-start label tree not ready");
            return BehaviorStatus.Running;
        }

        var s = ctx.Settings.Ultimatum;
        var overrides = UltimatumModDanger.ParseOverrides(s.ModDangerOverrides ?? new List<string>());

        switch (_preStartStage)
        {
            case PreStartStage.ReadType:
            {
                // Settle gate is ONLY active for the initial read. Once we've picked a mod
                // and advanced to ClickMod / ClickBegin, the panel may reflow (selection
                // highlight, transient empty mod list) and we shouldn't rewind progress.
                var liveMods = preStart.Modifiers;
                if (liveMods.Count == 0)
                {
                    _modsFirstPopulatedAt = DateTime.MinValue;
                    return BehaviorStatus.Running;
                }
                if (_modsFirstPopulatedAt == DateTime.MinValue)
                {
                    _modsFirstPopulatedAt = DateTime.UtcNow;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"mod panel populated ({liveMods.Count} options) — settling 500ms");
                    return BehaviorStatus.Running;
                }
                if ((DateTime.UtcNow - _modsFirstPopulatedAt).TotalMilliseconds < 500)
                    return BehaviorStatus.Running;

                var type = preStart.EncounterType;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"encounter type: '{type.Replace("\r\n", " | ")}'");

                var block = ctx.Strategy?.Block<UltimatumBlock>();
                if (!EncounterTypeAllowed(type, block, s.AbandonOnStandInCircle))
                {
                    _abandonRequested = true;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"ABANDON: disabled encounter type '{FirstLine(type)}'");
                    return BehaviorStatus.Failure;
                }

                var summary = string.Join(", ", liveMods.Select((m, i) => $"[{i}] {m.Id}={Danger(m, overrides)}"));
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum", $"mods: {summary}");

                var idx = PickBest(liveMods, overrides);
                if (idx < 0)
                {
                    _abandonRequested = true;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        "ABANDON: every modifier is NEVER-TAKE");
                    return BehaviorStatus.Failure;
                }

                _chosenModIndex = idx;
                _chosenModId    = liveMods[idx].Id;
                _chosenModDanger = Danger(liveMods[idx], overrides);
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"picked mod[{idx}] {_chosenModId} ({liveMods[idx].Name}) danger={_chosenModDanger}");
                _preStartStage = PreStartStage.ClickMod;
                return BehaviorStatus.Running;
            }

            case PreStartStage.ClickMod:
            {
                if ((DateTime.UtcNow - _lastPreStartClickAt).TotalMilliseconds < ClickThrottleMs)
                    return BehaviorStatus.Running;
                var rect = preStart.ModChoiceRect(_chosenModIndex);
                if (rect is null)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"ClickMod: mod[{_chosenModIndex}] rect is null — panel may have reflowed");
                    return BehaviorStatus.Running;
                }
                var (mx, my) = snap.Window.ToScreen(rect.Value.CenterX, rect.Value.CenterY);
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"ClickMod attempt {_modClickAttempts + 1}: mod[{_chosenModIndex}] at ({mx},{my})");
                var preModTicket = ctx.Input.Click(mx, my, ClickIntent.InteractUi, $"ultimatum: pick mod[{_chosenModIndex}] {_chosenModId}");
                if (!preModTicket.Accepted)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        "ClickMod suppressed by gate — retrying next tick");
                    return BehaviorStatus.Running;
                }
                _modClickAttempts++;
                _lastPreStartClickAt = DateTime.UtcNow;
                _preStartStage = PreStartStage.AwaitModSelected;
                return BehaviorStatus.Running;
            }

            case PreStartStage.AwaitModSelected:
            {
                var elapsedMs = (DateTime.UtcNow - _lastPreStartClickAt).TotalMilliseconds;
                // Live research showed SelectedChoice is pointer-like garbage on this
                // pre-start tree. Settle briefly, then verify the entire sequence using
                // the robust end state: BEGIN clears the pre-start panel.
                if (elapsedMs < ModToAcceptDelayMs || !ctx.Input.IsIdle)
                    return BehaviorStatus.Running;
                _preStartStage = PreStartStage.ClickBegin;
                return BehaviorStatus.Running;
            }

            case PreStartStage.ClickBegin:
            {
                if ((DateTime.UtcNow - _lastPreStartClickAt).TotalMilliseconds < ClickThrottleMs)
                    return BehaviorStatus.Running;
                var rect = preStart.BeginButtonRect;
                if (rect is null)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        "ClickBegin: BEGIN button rect is null — root.Children[6] missing");
                    return BehaviorStatus.Running;
                }
                var (bx, by) = snap.Window.ToScreen(rect.Value.CenterX, rect.Value.CenterY);
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"ClickBegin attempt {_beginClickAttempts + 1}: at ({bx},{by})");
                var beginTicket = ctx.Input.Click(bx, by, ClickIntent.InteractUi, "ultimatum: BEGIN");
                if (!beginTicket.Accepted)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        "ClickBegin suppressed by gate — retrying next tick");
                    return BehaviorStatus.Running;
                }
                _beginClickAttempts++;
                _beginEverDispatched = true;
                _lastPreStartClickAt = DateTime.UtcNow;
                _lastBeginClickAt    = DateTime.UtcNow;
                // Limited Arena halves on the FIRST successful BEGIN click after that mod
                // is picked. We track on click+1 attempt (idempotent: a redo doesn't re-halve
                // since _chosenModId is the latched value from this round only).
                if (string.Equals(_chosenModId, "Radius1", StringComparison.Ordinal) && _beginClickAttempts == 1)
                {
                    _currentArenaRadius *= 0.5f;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"arena radius halved → {_currentArenaRadius:F1} (Limited Arena picked)");
                }
                _preStartStage = PreStartStage.AwaitEncounterStart;
                return BehaviorStatus.Running;
            }

            case PreStartStage.AwaitEncounterStart:
            {
                if (_encounterStarted) return BehaviorStatus.Running;

                // Better encounter-started signals (the spawner's IsLabelVisible stays true
                // during the encounter — the hover label persists). Use any of:
                //   • UltimatumPanel.IsVisible — between-wave panel up = past wave 1.
                //   • preStart.Modifiers.Count == 0 — pre-start mod list cleared.
                // Either is unambiguous evidence that wave 1 is underway.
                var betweenWaveUp = ctx.Snapshot.UltimatumPanel.IsVisible;
                var preStartCleared = preStart.Modifiers.Count == 0;
                if (betweenWaveUp || preStartCleared)
                {
                    // LATCH _encounterStarted so we don't re-enter this case on subsequent
                    // ticks. UpdateSpawnerState would have set this too if IsTargetable
                    // flipped, but in current PoE the spawner stays targetable during the
                    // encounter, so we set the latch here from the UI-derived signal.
                    _encounterStarted = true;
                    _waveStartedAt = DateTime.UtcNow;
                    _cumulativeDanger += _chosenModDanger;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"BEGIN confirmed ({(betweenWaveUp ? "between-wave panel up" : "pre-start cleared")}) — encounter started, cumulative danger {_cumulativeDanger}");
                    StartBoundaryDiagnostic(ctx);
                    _preStartStage = PreStartStage.ReadType;
                    _lastBeginClickAt = DateTime.MinValue;
                    _modClickAttempts = 0;
                    _beginClickAttempts = 0;
                    _modsFirstPopulatedAt = DateTime.MinValue;
                    return BehaviorStatus.Running;
                }

                var elapsedMs = (DateTime.UtcNow - _lastPreStartClickAt).TotalMilliseconds;
                if ((DateTime.UtcNow - _lastVerifyLogAt).TotalMilliseconds > 600)
                {
                    _lastVerifyLogAt = DateTime.UtcNow;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"awaiting encounter start: mods={preStart.Modifiers.Count} panel={betweenWaveUp} elapsed={elapsedMs:F0}ms");
                }

                if (elapsedMs < BeginVerifyTimeoutMs) return BehaviorStatus.Running;
                if (_beginClickAttempts >= MaxClickAttempts)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"BEGIN unconfirmed after {_beginClickAttempts} dispatched attempts; staying in-map and retrying");
                    _beginClickAttempts = 0;
                }
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                    $"BEGIN unconfirmed (mods={preStart.Modifiers.Count} panel={betweenWaveUp}); reselecting modifier");
                _preStartStage = PreStartStage.ClickMod;
                return BehaviorStatus.Running;
            }
        }

        return BehaviorStatus.Running;
    }

    /// <summary>
    /// In-encounter between-wave panel handler. State machine:
    /// <list type="number">
    ///   <item><b>Decide</b> — read round counter, mod list, reward items. Decide action:
    ///         abandon (all 999), take-reward (round cap / danger cap / value below cutoff),
    ///         or pick lowest-danger mod + accept-trial.</item>
    ///   <item><b>ClickMod / AwaitModSelected</b> — click chosen mod, verify
    ///         <see cref="UltimatumPanelView.SelectedChoice"/> matches. Retry on timeout.</item>
    ///   <item><b>ClickAccept / AwaitWaveStart</b> — click Accept Trial, verify panel
    ///         <see cref="UltimatumPanelView.IsVisible"/> goes false. Retry on timeout.</item>
    ///   <item><b>ClickTakeReward / AwaitPanelClose</b> — click Take Reward, verify panel
    ///         gone. Retry on timeout.</item>
    /// </list>
    /// </summary>
    private BehaviorStatus HandleChoicePanel(BehaviorContext ctx)
    {
        var panel = ctx.Snapshot.UltimatumPanel;
        if (!panel.IsVisible)
        {
            // Panel closed mid-flow — reset for next opening.
            _inEncounterStage = InEncounterStage.Decide;
            return BehaviorStatus.Failure;
        }

        var s = ctx.Settings.Ultimatum;
        var overrides = UltimatumModDanger.ParseOverrides(s.ModDangerOverrides ?? new List<string>());

        switch (_inEncounterStage)
        {
            case InEncounterStage.Decide:
            {
                var mods = panel.Modifiers;
                if (mods.Count == 0) return BehaviorStatus.Running;   // panel still populating

                var (nextWave, maxWave) = panel.RoundProgress ?? (-1, -1);
                var nextRewardPath = ReadItemPath(ctx, panel.NextRewardItemAddress);
                var nextRewardValue = LookupItemChaos(nextRewardPath);

                var modSummary = string.Join(", ", mods.Select((m, i) => $"[{i}] {m.Id}={Danger(m, overrides)}"));
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                    $"completed={_roundsCompleted} next={nextWave}/{maxWave} " +
                    $"danger={_cumulativeDanger} mods: {modSummary}");
                if (!string.IsNullOrEmpty(nextRewardPath))
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        $"next reward: {ShortName(nextRewardPath)} ({nextRewardValue:F1}c)");

                // 1. All mods blocked → abandon (no acceptable next round).
                var idx = PickBest(mods, overrides);
                if (idx < 0)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        "TAKE REWARD: every modifier is NEVER-TAKE");
                    _inEncounterStage = InEncounterStage.ClickTakeReward;
                    return BehaviorStatus.Running;
                }

                // 2. Hard limit on round count.
                if (_roundsCompleted >= s.MaxWaves)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        $"TAKE REWARD: wave cap reached ({_roundsCompleted}/{s.MaxWaves} completed)");
                    _inEncounterStage = InEncounterStage.ClickTakeReward;
                    return BehaviorStatus.Running;
                }

                // 3. Cumulative danger projection — would picking this mod cross the budget?
                var pickedDanger = Danger(mods[idx], overrides);
                var projectedDanger = _cumulativeDanger + pickedDanger;
                if (projectedDanger > s.DangerThreshold)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        $"TAKE REWARD: projected danger {projectedDanger} > threshold {s.DangerThreshold}");
                    _inEncounterStage = InEncounterStage.ClickTakeReward;
                    return BehaviorStatus.Running;
                }

                // Otherwise continue. Pick the mod, advance to ClickMod.
                _inEncounterChosenIndex = idx;
                _inEncounterChosenId    = mods[idx].Id;
                _inEncounterChosenDanger = pickedDanger;
                _waveSequenceAttempts = 0;
                _waveSequenceStartedAt = DateTime.MinValue;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                    $"ACCEPT TRIAL: picked mod[{idx}] {_inEncounterChosenId} (danger {_inEncounterChosenDanger} → cumulative {projectedDanger})");
                _inEncounterStage = InEncounterStage.ClickMod;
                return BehaviorStatus.Running;
            }

            case InEncounterStage.ClickMod:
            {
                var rect = panel.ModChoiceRect(_inEncounterChosenIndex);
                if (rect is null) return BehaviorStatus.Running;
                var (mx, my) = ctx.Snapshot.Window.ToScreen(rect.Value.CenterX, rect.Value.CenterY);
                _waveSequenceAttempts++;
                _waveSequenceStartedAt = DateTime.UtcNow;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                    $"sequence attempt {_waveSequenceAttempts}: click mod[{_inEncounterChosenIndex}] at ({mx},{my})");
                var ieModTicket = ctx.Input.Click(mx, my, ClickIntent.InteractUi, $"ultimatum: pick mod[{_inEncounterChosenIndex}]");
                if (!ieModTicket.Accepted)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        "ClickMod suppressed by gate — retrying next tick");
                    return BehaviorStatus.Running;
                }
                _lastInEncounterClickAt = DateTime.UtcNow;
                _inEncounterStage = InEncounterStage.DelayBeforeAccept;
                return BehaviorStatus.Running;
            }

            case InEncounterStage.DelayBeforeAccept:
            {
                // Brief settle so the server registers the mod selection before we accept.
                // Skipping SelectedChoice verify entirely — the in-encounter panel's
                // SelectedChoice value is unreliable across rounds (carries stale value;
                // server update timing varies). End-state verify (panel close) is robust.
                if ((DateTime.UtcNow - _lastInEncounterClickAt).TotalMilliseconds < ModToAcceptDelayMs
                    || !ctx.Input.IsIdle)
                    return BehaviorStatus.Running;
                _inEncounterStage = InEncounterStage.ClickAccept;
                return BehaviorStatus.Running;
            }

            case InEncounterStage.ClickAccept:
            {
                var rect = panel.AcceptTrialButtonRect;
                if (rect is null) return BehaviorStatus.Running;
                var (ax, ay) = ctx.Snapshot.Window.ToScreen(rect.Value.CenterX, rect.Value.CenterY);
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                    $"click accept-trial at ({ax},{ay})");
                var acceptTicket = ctx.Input.Click(ax, ay, ClickIntent.InteractUi, "ultimatum: accept trial");
                if (!acceptTicket.Accepted)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        "ClickAccept suppressed by gate — retrying next tick");
                    // Don't advance stage; let the next tick retry.
                    return BehaviorStatus.Running;
                }
                _lastInEncounterClickAt = DateTime.UtcNow;
                if (string.Equals(_inEncounterChosenId, "Radius1", StringComparison.Ordinal) && _waveSequenceAttempts == 1)
                {
                    _currentArenaRadius *= 0.5f;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                        $"arena radius halved → {_currentArenaRadius:F1} (Limited Arena accepted)");
                }
                _inEncounterStage = InEncounterStage.AwaitWaveStart;
                return BehaviorStatus.Running;
            }

            case InEncounterStage.AwaitWaveStart:
            {
                // End-state verify: panel goes invisible when the wave starts. Robust
                // across rounds (no dependency on SelectedChoice).
                if (!panel.IsVisible)
                {
                    _cumulativeDanger += _inEncounterChosenDanger;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        $"wave starting (panel closed) — cumulative danger {_cumulativeDanger}");
                    _inEncounterStage = InEncounterStage.Decide;
                    return BehaviorStatus.Running;
                }
                // Heartbeat log every ~600ms.
                if ((DateTime.UtcNow - _lastVerifyLogAt).TotalMilliseconds > 600)
                {
                    _lastVerifyLogAt = DateTime.UtcNow;
                    var elapsed = (DateTime.UtcNow - _waveSequenceStartedAt).TotalMilliseconds;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        $"awaiting wave start: panel still visible, elapsed={elapsed:F0}ms attempt={_waveSequenceAttempts}");
                }
                // Timeout on the WHOLE click-mod + accept sequence (not per click). Restart
                // from ClickMod on timeout — covers both "mod click missed" and "accept click
                // missed" cases with a single retry path.
                if ((DateTime.UtcNow - _waveSequenceStartedAt).TotalMilliseconds < BeginVerifyTimeoutMs)
                    return BehaviorStatus.Running;
                if (_waveSequenceAttempts >= MaxClickAttempts)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        $"FALLBACK: wave-start sequence failed {_waveSequenceAttempts}x — taking reward instead");
                    _inEncounterStage = InEncounterStage.ClickTakeReward;
                    return BehaviorStatus.Running;
                }
                _inEncounterStage = InEncounterStage.ClickMod;
                return BehaviorStatus.Running;
            }

            case InEncounterStage.ClickTakeReward:
            {
                if ((DateTime.UtcNow - _lastInEncounterClickAt).TotalMilliseconds < ClickThrottleMs)
                    return BehaviorStatus.Running;
                var rect = panel.TakeRewardButtonRect;
                if (rect is null) return BehaviorStatus.Running;
                var (tx, ty) = ctx.Snapshot.Window.ToScreen(rect.Value.CenterX, rect.Value.CenterY);
                _inEncounterTakeClickAttempts++;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                    $"ClickTakeReward attempt {_inEncounterTakeClickAttempts} at ({tx},{ty})");
                var takeTicket = ctx.Input.Click(tx, ty, ClickIntent.InteractUi, "ultimatum: take reward");
                if (!takeTicket.Accepted)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        "ClickTakeReward suppressed by gate — retrying next tick");
                    return BehaviorStatus.Running;
                }
                _lastInEncounterClickAt = DateTime.UtcNow;
                _inEncounterStage = InEncounterStage.AwaitPanelClose;
                return BehaviorStatus.Running;
            }

            case InEncounterStage.AwaitPanelClose:
            {
                if (!panel.IsVisible)
                {
                    MarkEncounterDone("take reward confirmed (panel closed)");
                    return BehaviorStatus.Running;
                }
                if ((DateTime.UtcNow - _lastInEncounterClickAt).TotalMilliseconds < BeginVerifyTimeoutMs)
                    return BehaviorStatus.Running;
                if (_inEncounterTakeClickAttempts >= MaxClickAttempts)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/between-wave",
                        $"take-reward unconfirmed after {_inEncounterTakeClickAttempts} attempts; staying in-map and retrying");
                    _inEncounterTakeClickAttempts = 0;
                }
                _inEncounterStage = InEncounterStage.ClickTakeReward;
                return BehaviorStatus.Running;
            }
        }
        return BehaviorStatus.Running;
    }

    /// <summary>Read the entity's <c>Metadata/Items/...</c> path. Empty when the address is 0.</summary>
    private static string ReadItemPath(BehaviorContext ctx, nint entityAddress)
        => entityAddress == 0 ? string.Empty : EntityListReader.ReadEntityPath(ctx.Snapshot.Reader, entityAddress) ?? string.Empty;

    /// <summary>Look up chaos value for an item by its last-segment name. Returns 0 if no PriceCatalog wired.</summary>
    private static float LookupItemChaos(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return 0f;
        var prices = Behaviors.Loot.LootClosestVisible.SharedValueFilter?.GetType();   // sentinel — we don't have a direct PriceCatalog accessor here yet
        var name = ShortName(itemPath);
        // For v1, defer the price lookup wiring — we surface the path + value of 0 in logs.
        // Phase: pass a PriceCatalog via constructor and call prices.ValueChaos(name).
        _ = name;
        return 0f;
    }

    private static string ShortName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void MarkEncounterDone(string reason)
    {
        if (_encounterDone) return;
        _encounterDone = true;
        _encounterCompletedAt = DateTime.UtcNow;
        _movement.Release();
        BubblesBot.Bot.Diagnostics.EventLog.Emit(
            "Ultimatum", "ultimatum.completed", BubblesBot.Bot.Diagnostics.EventSeverity.Info,
            $"encounter complete: {reason}; settling and looting before exit");
    }

    private Core.Snapshot.EntityCache.Entry? PickSpawner(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return null;
        var ult = ctx.Snapshot.Ultimatum(ctx.Entities);
        return ult.Spawner;
    }

    private static SkillSlot? PickAttackSlot(BotSettings s)
    {
        if (s.Skills is null) return null;
        foreach (var slot in s.Skills.Slots)
        {
            if (slot.Role == SkillRole.Attack) return slot;
        }
        return null;
    }

    // ── Boundary-discovery diagnostic ────────────────────────────────────

    /// <summary>
    /// Freeze the current entity-id set + player-buff-name set; starts a 10-second window
    /// during which <see cref="TickBoundaryDiagnostic"/> reports anything new that
    /// appeared after BEGIN. Previous research couldn't find a readable arena-ring
    /// surface; if there's an "InsideUltimatum" buff, an "UltimatumArena" entity, or any
    /// other deterministic signal, this scope should surface it.
    /// </summary>
    private void StartBoundaryDiagnostic(BehaviorContext ctx)
    {
        _preEncounterEntityIds.Clear();
        _preEncounterBuffNames.Clear();
        _diagnosticLoggedEntities.Clear();

        if (ctx.Entities is not null)
        {
            foreach (var id in ctx.Entities.Entries.Keys) _preEncounterEntityIds.Add(id);
        }
        if (ctx.Snapshot.Player is { } p)
        {
            foreach (var b in p.Buffs.Buffs)
                if (!string.IsNullOrEmpty(b.Name)) _preEncounterBuffNames.Add(b.Name);
        }

        _diagnosticEndsAt = DateTime.UtcNow.AddSeconds(10);
        BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
            $"boundary-discovery recording started (10s; pre-entities={_preEncounterEntityIds.Count}, pre-buffs={_preEncounterBuffNames.Count})");
    }

    /// <summary>
    /// Per-tick during the discovery window: log any entity or buff that wasn't present at
    /// encounter-start. Caps log volume at ~40 entities total per run so a busy wave
    /// (Raging Dead spawns, Flame Spitters, etc.) doesn't drown the events panel.
    /// </summary>
    private void TickBoundaryDiagnostic(BehaviorContext ctx)
    {
        if (_diagnosticEndsAt == DateTime.MinValue) return;
        if (DateTime.UtcNow > _diagnosticEndsAt)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum",
                $"boundary-discovery recording ended ({_diagnosticLoggedEntities.Count} new entities logged)");
            _diagnosticEndsAt = DateTime.MinValue;
            return;
        }

        if (ctx.Entities is null) return;
        const int LogCap = 40;
        var anchor = _spawnerAnchor;

        foreach (var (id, e) in ctx.Entities.Entries)
        {
            if (_preEncounterEntityIds.Contains(id)) continue;
            if (_diagnosticLoggedEntities.Contains(id)) continue;
            if (_diagnosticLoggedEntities.Count >= LogCap) break;
            if (string.IsNullOrEmpty(e.Path)) continue;
            // Skip generic monster spawns — they flood the log. Look for STATIC/EFFECT-style
            // entities: anything NOT under Metadata/Monsters/ that's tagged as a possible
            // arena marker. Adjust the filter once we see what's actually in the stream.
            if (e.Path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)) continue;
            _diagnosticLoggedEntities.Add(id);
            int ax = anchor?.X ?? 0;
            int ay = anchor?.Y ?? 0;
            int dx = e.GridPosition.X - ax;
            int dy = e.GridPosition.Y - ay;
            var dist = MathF.Sqrt(dx * dx + dy * dy);
            BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/discovery",
                $"+entity id={id} dist={dist:F1} path={e.Path}");
        }

        // New player buffs — uncommon to spawn many, no cap.
        if (ctx.Snapshot.Player is { } p)
        {
            foreach (var b in p.Buffs.Buffs)
            {
                if (string.IsNullOrEmpty(b.Name)) continue;
                if (_preEncounterBuffNames.Contains(b.Name)) continue;
                _preEncounterBuffNames.Add(b.Name);   // dedupe so we log each only once
                BubblesBot.Bot.Diagnostics.EventLog.Log("Ultimatum/discovery",
                    $"+buff '{b.Name}' charges={b.Charges} timeRemaining={b.TimeRemaining:F1}s");
            }
        }
    }
}
