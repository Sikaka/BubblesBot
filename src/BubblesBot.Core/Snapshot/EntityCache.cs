using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Per-entity-ID cache that survives across world ticks. The hot insight: most of an entity's
/// data is *frozen* for its lifetime (path, kind, rarity, component addresses). Reading those
/// every tick is wasteful — the entity walk's real cost is resolving the component map. With
/// addresses cached, refreshing a 1000-mob field is just N small struct reads per tick.
///
/// <para>Lifecycle:
/// <list type="bullet">
///   <item><b>New entity</b>: not in cache → full hydrate (path, components, rarity, initial mutables). Costs ~50 µs.</item>
///   <item><b>Surviving entity</b>: in cache → re-read mutables only via cached component addresses. Costs ~5 µs.</item>
///   <item><b>Missing entity</b>: not seen for N walks → evicted.</item>
/// </list>
/// </para>
///
/// <para>Tier-rate scheduling (Hot/Warm/Cold) is a future addition on top of this. The basic
/// cache alone gets ~5× speedup on dense maps because it eliminates per-tick component-map
/// resolution.</para>
/// </summary>
public sealed class EntityCache
{
    public sealed record ScanHealth(
        long Tick,
        int EntityAddresses,
        int NodesVisited,
        int TraversalBadReads,
        bool HitSafetyLimit,
        int IdReadFailures,
        int HydrationFailures,
        int MutableRefreshFailures)
    {
        public bool Healthy => TraversalBadReads == 0 && !HitSafetyLimit;
    }

    /// <summary>
    /// Eviction policy is now smart, not time-based. We only evict when:
    ///   • The entity is missing from the live entity list, AND
    ///   • Its cached grid position is INSIDE the network bubble (so PoE *should* be
    ///     streaming it; if it's missing in-bubble, it's genuinely gone — died, picked up,
    ///     consumed mechanic).
    /// Entities outside the bubble stay forever (until area change). That preserves
    /// "I know there's a shrine over there I haven't taken yet" across long walks.
    /// </summary>
    private const int InBubbleMissedWalksToEvict = 4;

    /// <summary>
    /// Per-entity refresh priority. Bubble + alive + (moving OR rare/unique) → Hot (every
    /// tick). In bubble + alive + idle → Warm (every 3 ticks). Outside bubble or dead →
    /// Cold (every 8 ticks). Re-evaluated on each refresh — if a Cold mob becomes Hot
    /// (player walks within range) it'll get bumped to Hot on its next due tick.
    /// </summary>
    public enum Tier { Cold, Warm, Hot }

    private const int HotIntervalTicks  = 1;   // ~30 Hz
    private const int WarmIntervalTicks = 3;   // ~10 Hz
    private const int ColdIntervalTicks = 8;   // ~3.75 Hz

    /// <summary>Walks a state-machine mechanic gets its component map re-read when the map
    /// came back without StateMachine (streaming race). Budget resets when the entity
    /// returns to range, so every fresh approach gets another chance.</summary>
    private const int MaxComponentRehydrateAttempts = 16;

    /// <summary>~1 s: how often a LIVE monster that reads untargetable-or-allied gets its component
    /// map re-resolved. Components can relocate when a dormant-spawned mob activates (Positioned/
    /// Targetable move while Life stays put), leaving the frozen pointers stale — this heals it.</summary>
    private const int ComponentRefreshIntervalTicks = 30;

    /// <summary>One cached entity. Frozen fields read once at hydrate; mutables refreshed per tick.</summary>
    public sealed class Entry
    {
        // Identity (frozen)
        public nint Address;
        public uint Id;

        // Frozen — read once on first sight
        public string                          Path = string.Empty;
        public string                          Metadata = string.Empty;
        public EntityListReader.EntityKind     Kind;
        public EntityListReader.EntityRarity?  Rarity;
        public IReadOnlyDictionary<string, nint> Components = new Dictionary<string, nint>();
        public nint LifeCompAddr;
        public nint PositionedCompAddr;
        public nint PathfindingCompAddr;
        public nint StateMachineCompAddr;
        public nint OmpCompAddr;
        public nint TargetableCompAddr;
        public nint RenderCompAddr;
        public Vector3 RenderPosition;
        public Vector3 RenderBounds;
        public bool RenderGeometryReadable;
        public nint ChestCompAddr;
        public nint BuffsCompAddr;
        public nint ShrineCompAddr;
        /// <summary>Display name from Render component (e.g. "Carius, the Unnatural"). Empty for unnamed mobs.</summary>
        public string Name = string.Empty;

        /// <summary>Attempts spent re-reading a component map that hydrated without the
        /// StateMachine component (see MaxComponentRehydrateAttempts).</summary>
        public int HydrationRetries;

        /// <summary>Tick of the last "alive but reads untargetable/allied" component re-resolve
        /// (rate-limited by ComponentRefreshIntervalTicks). Heals dormant-spawned mobs whose
        /// Positioned/Targetable components relocated on activation.</summary>
        public int LastComponentRefreshTick;

        // Mutable — refreshed each world tick the entity is in scope
        public Vector2i GridPosition;
        public int      HpCurrent;
        public int      HpMax;
        public bool     IsMoving;
        public bool     IsTargetable = true;
        public BooleanObservation Targetability = BooleanObservation.Unknown(
            "Targetable.IsTargetable", 0, ObservationReadStatus.NeverRead);
        /// <summary>
        /// True when PoE's reaction byte (Positioned +0x1E0, live-verified 2026-07-14)
        /// marks this entity allied to the player: own minions, shrine-summoned helpers,
        /// Blink Arrow clones, quest allies. Mutable — dominion/charm effects can flip a
        /// monster's allegiance mid-fight.
        /// </summary>
        public bool     IsAllied;
        public BooleanObservation AlliedReaction = BooleanObservation.Unknown(
            "Positioned.Reaction", 0, ObservationReadStatus.NeverRead,
            ObservationConfidence.Validated);
        /// <summary>
        /// True while the entity carries a state buff (hidden_monster*, frozen_in_time*)
        /// that makes it un-fightable RIGHT NOW: dormant Vaal constructs, submerged mobs,
        /// essence-imprisoned monsters. Live state, re-read per refresh — a released
        /// essence mob becomes a valid target the moment the buff drops.
        /// </summary>
        public bool     IsDormant;
        public BooleanObservation Dormancy = BooleanObservation.Unknown(
            "Buffs.InvalidTargetBuff", 0, ObservationReadStatus.NeverRead);
        public BooleanObservation LifeReadable = BooleanObservation.Unknown(
            "Life.Health", 0, ObservationReadStatus.NeverRead,
            ObservationConfidence.Validated);
        /// <summary>
        /// EXPERIMENTAL / currently UNUSED. Reads <c>Entity.Flags &amp; 0x400</c> (== 0). This
        /// briefly looked like ExileCore's <c>Entity.IsHidden</c> — it matched 15/15 on one
        /// snapshot — but that was correlation: dormant mobs happened to be non-animating and
        /// spawned mobs animating. Live, the bit tracks ACTIVE/ANIMATING state, not spawn-state,
        /// so idle-but-real mobs false-flag as hidden. NOT reliable for "don't target." Dormant/
        /// garbage mobs are handled by the combat damage-gate (blacklist no-damage targets), not
        /// this flag. Kept only to document the offset; do not gate targeting on it. Finding the
        /// real IsHidden needs a single-entity before/after diff (a mob flipping on approach).
        /// </summary>
        public bool     IsHidden;
        /// <summary>For Chest entities: true once the chest has been opened. Read from
        /// <c>Chest.IsOpened</c> (component +0x168). Note: chests stay <c>IsTargetable=true</c>
        /// after opening, so consumers must check <c>IsOpened</c> not targetable.</summary>
        public bool     IsOpened;
        /// <summary>Dedicated Shrine component state. Unlike Targetable, this flips from
        /// available to consumed when the player takes the shrine buff.</summary>
        public BooleanObservation ShrineAvailable = BooleanObservation.Unknown(
            "Shrine.IsAvailable", 0, ObservationReadStatus.NeverRead,
            ObservationConfidence.Validated);
        /// <summary>RitualRuneInteractable.current_state: 1 fresh, 2 active, 3 complete.</summary>
        public LongObservation RitualCurrentState = LongObservation.Unknown(
            "Ritual.current_state", 0, ObservationReadStatus.NeverRead,
            ObservationConfidence.Validated);
        /// <summary>RitualRuneInteractable.interaction_enabled: 1 when start-click is allowed.</summary>
        public LongObservation RitualInteractionEnabled = LongObservation.Unknown(
            "Ritual.interaction_enabled", 0, ObservationReadStatus.NeverRead,
            ObservationConfidence.Validated);
        /// <summary>Delirium mirror <c>interacted</c>: 0 before crossing, 1 after activation.</summary>
        public LongObservation DeliriumMirrorInteracted = LongObservation.Unknown(
            "Delirium.interacted", 0, ObservationReadStatus.NeverRead,
            ObservationConfidence.Validated);
        /// <summary>
        /// Raw Afflictionator StateMachine values retained for live phase diffing. The array
        /// reference is only replaced when a value changes, keeping flight-recorder deltas small.
        /// </summary>
        public long[] SimulacrumRawStates = Array.Empty<long>();
        public LongObservation SimulacrumActive = LongObservation.Unknown(
            "Simulacrum.active", 0, ObservationReadStatus.NeverRead);
        public LongObservation SimulacrumGoodbye = LongObservation.Unknown(
            "Simulacrum.goodbye", 0, ObservationReadStatus.NeverRead);
        public LongObservation SimulacrumWave = LongObservation.Unknown(
            "Simulacrum.wave", 0, ObservationReadStatus.NeverRead);

        // Bookkeeping
        public int  MissedWalks;
        public Tier Tier = Tier.Hot;     // start hot — first refresh forces a full read anyway
        public int  LastRefreshedTick;   // _tickCounter value at last RefreshMutable
        public DateTime LastSeenAt = DateTime.UtcNow;   // wall-clock timestamp of last successful traversal hit
        /// <summary>
        /// True when we haven't observed this entity in the live entity list for at least
        /// one walk. Cached frozen + last-known-mutable fields are preserved; consumers
        /// reading <see cref="GridPosition"/>/<see cref="HpCurrent"/> get the last-good
        /// values, not freshly-read garbage. Use this to gate "refresh from memory" vs.
        /// "trust cache" decisions.
        /// </summary>
        public bool IsStale => MissedWalks > 0;

        /// <summary>
        /// True iff PoE attached a Life component to this entity. Real monsters (hostile +
        /// friendly) all have one; effect entities (Volatile Orbs, particle markers, AoE
        /// indicators) do not. Use as a cheap, frozen-at-hydration gate when filtering for
        /// "this is a real fight target" — avoids the IsTargetable offset which is unverified
        /// across all entity types.
        /// </summary>
        public bool HasLife => LifeCompAddr != 0;

        /// <summary>
        /// How the bot relates to this entity — classified once at hydration from the
        /// metadata path via <see cref="Knowledge.EntityDispositionCatalog"/>. Ignore =
        /// never a target (daemons, mirages, friendly masters…); Hazard = never a target
        /// but track for avoidance (volatile cores).
        /// </summary>
        public Knowledge.EntityDisposition Disposition = Knowledge.EntityDisposition.Combatant;

        // The reaction byte is the authoritative ally check (shrine-summoned skeletons
        // share hostile mobs' metadata paths, so the catalog can't catch them); the
        // disposition catalog handles what the byte can't: hazards and undamageable noise
        // species, which read "not allied" but still must not be targeted.
        public bool IsHostileMonster => Kind == EntityListReader.EntityKind.Monster
            && !IsAllied
            && Disposition == Knowledge.EntityDisposition.Combatant;

        /// <summary>Unkillable-but-dangerous entity (volatile core etc.) — dodge, don't fight.</summary>
        public bool IsHazard => Kind == EntityListReader.EntityKind.Monster
            && Disposition == Knowledge.EntityDisposition.Hazard;

        public bool IsAlive => HpCurrent > 0 && HpMax > 0;
    }

    private readonly MemoryReader _reader;
    private readonly Dictionary<uint, Entry> _byId = new();
    private int _tickCounter;
    public ScanHealth LastScanHealth { get; private set; } = new(0, 0, 0, 0, false, 0, 0, 0);

    public EntityCache(MemoryReader reader) { _reader = reader; }

    /// <summary>Diagnostic: per-tier entity counts.</summary>
    public (int hot, int warm, int cold, int total) TierBreakdown()
    {
        int h = 0, w = 0, c = 0;
        foreach (var e in _byId.Values)
        {
            if (e.Tier == Tier.Hot) h++;
            else if (e.Tier == Tier.Warm) w++;
            else c++;
        }
        return (h, w, c, _byId.Count);
    }

    /// <summary>All currently-known entities. Iterate from the renderer for cheap dot-list builds.</summary>
    public IReadOnlyDictionary<uint, Entry> Entries => _byId;

    /// <summary>Drop everything. Call on area transitions — old IDs may collide with new instances.</summary>
    public void Clear() => _byId.Clear();

    /// <summary>
    /// Walk the live entity tree, hydrate new entities, refresh mutable fields per tier,
    /// evict entries that disappeared. Call once per world tick.
    ///
    /// <para><paramref name="playerGrid"/> drives tier classification — entities outside
    /// the network bubble fall to Cold and only refresh every 8 ticks.</para>
    /// </summary>
    public void Refresh(nint entityListAddress, Vector2i playerGrid)
    {
        if (entityListAddress == 0) return;
        _tickCounter++;

        var traversal = EntityListReader.EnumerateEntityAddresses(_reader, entityListAddress, maxNodes: 5000);
        var idReadFailures = 0;
        var hydrationFailures = 0;
        var mutableRefreshFailures = 0;
        var traversalHealthy = traversal.BadReads == 0 && !traversal.HitSafetyLimit;

        if (traversalHealthy)
            foreach (var entry in _byId.Values) entry.MissedWalks++;

        foreach (var addr in traversal.EntityAddresses)
        {
            // Just the ID — one tiny read per entity per tick. Discovery is unconditional;
            // mutable refresh is rate-limited per tier.
            if (!_reader.TryReadStruct<uint>(addr + KnownOffsets.Entity.Id, out var id) || id == 0)
            {
                idReadFailures++;
                continue;
            }

            if (!_byId.TryGetValue(id, out var entry))
            {
                entry = TryHydrate(addr, id);
                if (entry is null) { hydrationFailures++; continue; }
                _byId[id] = entry;
            }
            else if (entry.Address != addr)
            {
                // Same ID at a new address — likely respawn/relocation. Re-hydrate.
                var fresh = TryHydrate(addr, id);
                if (fresh is null) { hydrationFailures++; continue; }
                _byId[id] = fresh;
                entry = fresh;
            }
            else if (entry.Path.Length == 0)
            {
                // Hydration raced entity streaming — the path pointer wasn't initialized yet,
                // which would otherwise freeze Kind=Unknown for the entity's lifetime (live
                // incident 2026-07-15: a freshly cast town portal never matched
                // EntityKind.TownPortal and the leave-map flow hard-failed). Keep re-hydrating
                // until the path reads non-empty.
                var fresh = TryHydrate(addr, id);
                if (fresh is not null && fresh.Path.Length > 0)
                {
                    _byId[id] = fresh;
                    entry = fresh;
                }
            }
            else if (entry.StateMachineCompAddr == 0
                && entry.HydrationRetries < MaxComponentRehydrateAttempts
                && NeedsStateMachine(entry.Path))
            {
                // Same streaming race, partial flavor: the component map read missed
                // StateMachine, and frozen at 0 it leaves ritual/pump/altar state Unknown
                // for the entity's lifetime (live incident 2026-07-15: all four ritual
                // runes stayed Unknown while the player stood on them; rituals never
                // started, the reward shop never opened). Bounded retries — entities
                // that genuinely lack the component stop costing reads.
                entry.HydrationRetries++;
                var fresh = TryHydrate(addr, id);
                if (fresh is not null && fresh.StateMachineCompAddr != 0)
                {
                    _byId[id] = fresh;
                    entry = fresh;
                }
            }
            else if (entry.Kind == EntityListReader.EntityKind.Monster
                && entry.HpCurrent > 0
                && (entry.Targetability.Truth != ObservationTruth.True || entry.IsAllied)
                && _tickCounter - entry.LastComponentRefreshTick >= ComponentRefreshIntervalTicks)
            {
                // Stale-relocated-component heal (Kosis boss, 2026-07-16). A monster that streamed
                // in dormant/pre-spawn can have its Positioned/Targetable components RELOCATED when
                // it activates while Life stays put — so HP reads right but targetable/reaction read
                // from the frozen dormant pointers (untargetable/allied). A LIVE monster reading
                // untargetable-or-allied is that signature: re-hydrate from the current entity
                // address to pick up the relocated components. Rate-limited to ~1/s per entity, so a
                // genuine allied minion / untargetable prop costs one cheap re-read per second and a
                // boss self-corrects within a second of activation / each phase flip.
                var fresh = TryHydrate(addr, id);
                if (fresh is not null)
                {
                    fresh.LastComponentRefreshTick = _tickCounter;
                    _byId[id] = fresh;
                    entry = fresh;
                }
                else entry.LastComponentRefreshTick = _tickCounter;
            }

            // TEMP DIAGNOSTIC (Kosis boss read): log cached-vs-live address + component reads.
            if (entry.Path.Contains("AfflictionDemonBoss", StringComparison.Ordinal)
                && _tickCounter % 30 == 0)
            {
                var freshMap = EntityComponents.ReadComponentMap(_reader, addr);
                freshMap.TryGetValue("Positioned", out var freshPos);
                byte freshReaction = 255;
                if (freshPos != 0) _reader.TryReadStruct<byte>(freshPos + KnownOffsets.PositionedComponent.Reaction, out freshReaction);
                Console.Error.WriteLine(
                    $"[BOSSDIAG] id={id} entryAddr=0x{(long)entry.Address:X} liveAddr=0x{(long)addr:X} match={entry.Address == addr} "
                    + $"cachedPos=0x{(long)entry.PositionedCompAddr:X} cachedAllied={entry.IsAllied} cachedTgt={entry.Targetability.Truth} hp={entry.HpCurrent} "
                    + $"| freshWalkPos=0x{(long)freshPos:X} freshReaction=0x{freshReaction:X2}");
            }

            // Returning-to-range cleanup: if this entry was stale and we just saw it again,
            // refresh aggressively (force the dueInterval gate by zeroing LastRefreshedTick).
            // PoE may have updated HP / position / state-machine while we weren't watching;
            // a full re-read corrects any drift before the consumer touches it.
            var wasStale = entry.MissedWalks > 0;
            entry.MissedWalks = 0;
            entry.LastSeenAt = DateTime.UtcNow;
            if (wasStale)
            {
                entry.LastRefreshedTick = 0;  // force refresh this tick
                // Fresh approach = fresh chance for a component map that never resolved.
                if (entry.StateMachineCompAddr == 0 && NeedsStateMachine(entry.Path))
                    entry.HydrationRetries = 0;
            }

            // Tier-rate refresh. Each entity has its own due cadence — Hot mobs refresh
            // every tick, Cold mobs every 8th. Re-classify after refresh so a Cold mob
            // that becomes engaged (player walks into range) bumps to Hot on the next pass.
            var dueInterval = entry.Tier switch
            {
                Tier.Hot  => HotIntervalTicks,
                Tier.Warm => WarmIntervalTicks,
                _         => ColdIntervalTicks,
            };
            if (_tickCounter - entry.LastRefreshedTick >= dueInterval)
            {
                try { RefreshMutable(entry); }
                catch { mutableRefreshFailures++; /* stale entity memory — keep previous values */ }
                entry.LastRefreshedTick = _tickCounter;
                entry.Tier = ClassifyTier(entry, playerGrid);
            }
        }

        // Smart eviction. We keep stale entries forever within an area UNLESS:
        //   • Their cached grid is inside the network bubble (PoE should be streaming
        //     them), AND
        //   • They've been missing for at least InBubbleMissedWalksToEvict walks.
        // That combination = "we should be seeing them but they're gone" → genuine death/
        // pickup/consumption → evict.
        //
        // Out-of-bubble entries are kept indefinitely so re-entering range can pick them
        // back up cleanly (the wasStale path above triggers a fresh re-read).
        if (traversalHealthy && _byId.Count > 0)
        {
            var bubble2 = (long)Pathfinding.GridConstants.NetworkBubbleGrid * Pathfinding.GridConstants.NetworkBubbleGrid;
            List<uint>? evict = null;
            foreach (var (id, entry) in _byId)
            {
                if (entry.MissedWalks <= InBubbleMissedWalksToEvict) continue;
                var dx = entry.GridPosition.X - playerGrid.X;
                var dy = entry.GridPosition.Y - playerGrid.Y;
                var inBubble = (long)dx * dx + (long)dy * dy <= bubble2;
                if (inBubble) (evict ??= new List<uint>()).Add(id);
            }
            if (evict is not null) foreach (var id in evict) _byId.Remove(id);
        }

        LastScanHealth = new ScanHealth(
            _tickCounter,
            traversal.EntityAddresses.Count,
            traversal.NodesVisited,
            traversal.BadReads,
            traversal.HitSafetyLimit,
            idReadFailures,
            hydrationFailures,
            mutableRefreshFailures);
    }

    private static Tier ClassifyTier(Entry e, Vector2i playerGrid)
    {
        if (!e.IsAlive) return Tier.Cold;

        var dx = e.GridPosition.X - playerGrid.X;
        var dy = e.GridPosition.Y - playerGrid.Y;
        var bubble2 = (long)Pathfinding.GridConstants.NetworkBubbleGrid * Pathfinding.GridConstants.NetworkBubbleGrid;
        if ((long)dx * dx + (long)dy * dy > bubble2) return Tier.Cold;

        // Anything actively pathing or rare-or-better is "interesting" → Hot.
        if (e.IsMoving) return Tier.Hot;
        if (e.Rarity is EntityListReader.EntityRarity.Rare or EntityListReader.EntityRarity.Unique)
            return Tier.Hot;

        return Tier.Warm;
    }

    /// <summary>
    /// Mechanic entities whose state reads require the StateMachine component. A frozen 0
    /// address on THESE is a streaming-race casualty worth re-hydrating; the many entity
    /// types that legitimately have no StateMachine are left alone.
    /// </summary>
    private static bool NeedsStateMachine(string path)
        => path.Contains("/Ritual/RitualRuneInteractable", StringComparison.Ordinal)
        || path.EndsWith("/BlightPump", StringComparison.Ordinal)
        || path.EndsWith("/Affliction/AfflictionInitiator", StringComparison.Ordinal)
        || path.Contains("PrimordialBosses/TangleAltar", StringComparison.Ordinal)
        || path.Contains("PrimordialBosses/CleansingFireAltar", StringComparison.Ordinal);

    /// <summary>One-time read of all frozen fields plus a first read of mutables.</summary>
    private Entry? TryHydrate(nint addr, uint id)
    {
        try
        {
            var path     = EntityListReader.ReadEntityPath(_reader, addr) ?? string.Empty;
            var metadata = path;
            var split    = metadata.IndexOf('@');
            if (split >= 0) metadata = metadata[..split];

            var components = EntityComponents.ReadComponentMap(_reader, addr);

            var entry = new Entry
            {
                Address    = addr,
                Id         = id,
                Path       = path,
                Metadata   = metadata,
                Kind       = ClassifyKind(metadata, components),
                Disposition = Knowledge.EntityDispositionCatalog.Classify(metadata),
                Components = components,
            };

            components.TryGetValue("Life",         out entry.LifeCompAddr);
            components.TryGetValue("Positioned",   out entry.PositionedCompAddr);
            components.TryGetValue("Pathfinding",  out entry.PathfindingCompAddr);
            components.TryGetValue("StateMachine", out entry.StateMachineCompAddr);
            components.TryGetValue("ObjectMagicProperties", out entry.OmpCompAddr);
            components.TryGetValue("Targetable",   out entry.TargetableCompAddr);
            components.TryGetValue("Render",       out entry.RenderCompAddr);
            components.TryGetValue("Chest",        out entry.ChestCompAddr);
            components.TryGetValue("Buffs",        out entry.BuffsCompAddr);
            components.TryGetValue("Shrine",       out entry.ShrineCompAddr);

            // Display name (Render +0x148 NativeString). Frozen — names don't change for an
            // entity's lifetime. Unique/rare mobs get this; trash returns empty.
            if (entry.RenderCompAddr != 0)
                entry.Name = NativeString.Read(_reader, entry.RenderCompAddr + KnownOffsets.RenderComponent.Name);
            entry.Disposition = Knowledge.EntityDispositionCatalog.Classify(metadata, entry.Name);

            // Rarity is frozen for a monster's lifetime. Read once.
            if (entry.OmpCompAddr != 0
                && _reader.TryReadStruct<int>(entry.OmpCompAddr + KnownOffsets.ObjectMagicPropertiesComponent.Rarity, out var r)
                && r is >= 0 and <= 4)
            {
                entry.Rarity = (EntityListReader.EntityRarity)r;
            }

            RefreshMutable(entry);
            return entry;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-read just the fields that change. Uses cached component addresses, so it's just
    /// a handful of <see cref="MemoryReader.TryReadStruct{T}"/> calls per entity per tick.
    /// </summary>
    private void RefreshMutable(Entry e)
    {
        // Hidden/dormant flag — lives on the entity itself (not a component). Mutable: flips when
        // the mob activates. Shown bit SET = active; CLEAR = hidden.
        if (_reader.TryReadStruct<uint>(e.Address + KnownOffsets.Entity.Flags, out var flags))
            e.IsHidden = (flags & KnownOffsets.Entity.ShownFlagBit) == 0;

        if (e.PositionedCompAddr != 0)
        {
            _reader.TryReadStruct(e.PositionedCompAddr + KnownOffsets.PositionedComponent.GridPosition, out e.GridPosition);
            if (_reader.TryReadStruct<byte>(e.PositionedCompAddr + KnownOffsets.PositionedComponent.Reaction, out var reaction))
            {
                e.IsAllied = reaction != 0;
                e.AlliedReaction = BooleanObservation.Known(
                    e.IsAllied, "Positioned.Reaction", _tickCounter, ObservationConfidence.Validated);
            }
            else e.AlliedReaction = BooleanObservation.Unknown(
                "Positioned.Reaction", _tickCounter, ObservationReadStatus.ReadFailed,
                ObservationConfidence.Validated);
        }
        else e.AlliedReaction = BooleanObservation.Unknown(
            "Positioned.Reaction", _tickCounter, ObservationReadStatus.MissingComponent,
            ObservationConfidence.Validated);

        if (e.LifeCompAddr != 0
            && _reader.TryReadStruct<VitalStruct>(e.LifeCompAddr + KnownOffsets.LifeComponent.Health, out var hp)
            && hp.LooksValid())
        {
            e.HpCurrent = hp.Current;
            e.HpMax     = hp.Max;
            e.LifeReadable = BooleanObservation.Known(
                true, "Life.Health", _tickCounter, ObservationConfidence.Validated);
        }
        else e.LifeReadable = BooleanObservation.Unknown(
            "Life.Health", _tickCounter,
            e.LifeCompAddr == 0 ? ObservationReadStatus.MissingComponent : ObservationReadStatus.ReadFailed,
            ObservationConfidence.Validated);

        if (e.PathfindingCompAddr != 0
            && _reader.TryReadStruct<byte>(e.PathfindingCompAddr + KnownOffsets.PathfindingComponent.IsMoving, out var moving))
        {
            e.IsMoving = moving != 0;
        }

        e.RenderGeometryReadable = e.RenderCompAddr != 0
            && _reader.TryReadStruct(e.RenderCompAddr + KnownOffsets.RenderComponent.Pos, out e.RenderPosition)
            && _reader.TryReadStruct(e.RenderCompAddr + KnownOffsets.RenderComponent.Bounds, out e.RenderBounds);

        // Buff-state target validity — only evaluated for live combatant hostiles (the
        // only consumers) to keep the refresh path lean.
        if (e.BuffsCompAddr != 0
            && e.Kind == EntityListReader.EntityKind.Monster
            && e.HpCurrent > 0
            && !e.IsAllied
            && e.Disposition == Knowledge.EntityDisposition.Combatant)
        {
            if (EntityBuffs.TryHasInvalidTargetBuff(_reader, e.BuffsCompAddr, out var dormant))
            {
                e.IsDormant = dormant;
                e.Dormancy = BooleanObservation.Known(
                    dormant, "Buffs.InvalidTargetBuff", _tickCounter, ObservationConfidence.Experimental);
            }
            else e.Dormancy = BooleanObservation.Unknown(
                "Buffs.InvalidTargetBuff", _tickCounter, ObservationReadStatus.ReadFailed);
        }
        else if (e.Kind == EntityListReader.EntityKind.Monster)
            e.Dormancy = BooleanObservation.Known(
                false, "Buffs.InvalidTargetBuff", _tickCounter, ObservationConfidence.Experimental);

        if (e.TargetableCompAddr != 0
            && _reader.TryReadStruct<byte>(e.TargetableCompAddr + KnownOffsets.TargetableComponent.IsTargetable, out var targetable))
        {
            e.IsTargetable = targetable != 0;
            e.Targetability = BooleanObservation.Known(
                e.IsTargetable, "Targetable.IsTargetable", _tickCounter, ObservationConfidence.Experimental);
        }
        else e.Targetability = BooleanObservation.Unknown(
            "Targetable.IsTargetable", _tickCounter,
            e.TargetableCompAddr == 0 ? ObservationReadStatus.MissingComponent : ObservationReadStatus.ReadFailed);

        if (e.ChestCompAddr != 0
            && _reader.TryReadStruct<byte>(e.ChestCompAddr + KnownOffsets.ChestComponent.IsOpened, out var opened))
        {
            e.IsOpened = opened != 0;
        }

        if (e.Kind == EntityListReader.EntityKind.Shrine)
        {
            if (e.ShrineCompAddr != 0
                && _reader.TryReadStruct<byte>(e.ShrineCompAddr + KnownOffsets.ShrineComponent.IsUnavailable,
                    out var unavailable)
                && unavailable is 0 or 1)
            {
                e.ShrineAvailable = BooleanObservation.Known(
                    unavailable == 0, "Shrine.IsAvailable", _tickCounter,
                    ObservationConfidence.Validated);
            }
            else e.ShrineAvailable = BooleanObservation.Unknown(
                "Shrine.IsAvailable", _tickCounter,
                e.ShrineCompAddr == 0 ? ObservationReadStatus.MissingComponent : ObservationReadStatus.ReadFailed,
                ObservationConfidence.Validated);
        }

        if (e.Path.Contains("/Ritual/RitualRuneInteractable", StringComparison.Ordinal))
        {
            e.RitualCurrentState = StateMachineView.ObserveValue(
                _reader, e.StateMachineCompAddr, RitualStates.RuneInteractable.CurrentState,
                _tickCounter, "Ritual.current_state");
            e.RitualInteractionEnabled = StateMachineView.ObserveValue(
                _reader, e.StateMachineCompAddr, RitualStates.RuneInteractable.InteractionEnabled,
                _tickCounter, "Ritual.interaction_enabled");
        }

        if (e.Path.EndsWith("/Affliction/AfflictionInitiator", StringComparison.Ordinal))
        {
            e.DeliriumMirrorInteracted = StateMachineView.ObserveValue(
                _reader, e.StateMachineCompAddr, DeliriumStates.Mirror.Interacted,
                _tickCounter, "Delirium.interacted");
        }

        if (e.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase))
        {
            var raw = StateMachineView.ReadValues(
                _reader, e.StateMachineCompAddr, SimulacrumStates.Monolith.CaptureCount);
            if (!e.SimulacrumRawStates.AsSpan().SequenceEqual(raw))
                e.SimulacrumRawStates = raw;

            if (SimulacrumStates.Monolith.IsValidated)
            {
                e.SimulacrumActive = StateMachineView.ObserveValue(
                    _reader, e.StateMachineCompAddr, SimulacrumStates.Monolith.Active,
                    _tickCounter, "Simulacrum.active");
                e.SimulacrumGoodbye = StateMachineView.ObserveValue(
                    _reader, e.StateMachineCompAddr, SimulacrumStates.Monolith.Goodbye,
                    _tickCounter, "Simulacrum.goodbye");
                e.SimulacrumWave = StateMachineView.ObserveValue(
                    _reader, e.StateMachineCompAddr, SimulacrumStates.Monolith.Wave,
                    _tickCounter, "Simulacrum.wave");
            }
            else
            {
                e.SimulacrumActive = LongObservation.Unknown(
                    "Simulacrum.active", _tickCounter, ObservationReadStatus.InvalidValue);
                e.SimulacrumGoodbye = LongObservation.Unknown(
                    "Simulacrum.goodbye", _tickCounter, ObservationReadStatus.InvalidValue);
                e.SimulacrumWave = LongObservation.Unknown(
                    "Simulacrum.wave", _tickCounter, ObservationReadStatus.InvalidValue);
            }
        }
    }

    private static EntityListReader.EntityKind ClassifyKind(string metadata, IReadOnlyDictionary<string, nint> components)
    {
        if (components.ContainsKey("Player")) return EntityListReader.EntityKind.Player;
        if (components.ContainsKey("WorldItem")) return EntityListReader.EntityKind.WorldItem;
        if (metadata.StartsWith("Metadata/Monsters/", StringComparison.OrdinalIgnoreCase)) return EntityListReader.EntityKind.Monster;
        if (metadata.StartsWith("Metadata/Chests/", StringComparison.OrdinalIgnoreCase)) return EntityListReader.EntityKind.Chest;
        if (metadata.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase)) return EntityListReader.EntityKind.AreaTransition;
        if (metadata.Contains("TownPortal", StringComparison.OrdinalIgnoreCase)
            || metadata.Contains("MultiplexPortal", StringComparison.OrdinalIgnoreCase))
            return EntityListReader.EntityKind.TownPortal;
        if (metadata.Contains("Portal", StringComparison.OrdinalIgnoreCase)) return EntityListReader.EntityKind.Portal;
        if (metadata.Contains("MiscellaneousObjects/Stash", StringComparison.OrdinalIgnoreCase)
            || metadata.EndsWith("/StashPoof", StringComparison.OrdinalIgnoreCase))
            return EntityListReader.EntityKind.Stash;
        if (metadata.Contains("Shrine", StringComparison.OrdinalIgnoreCase)) return EntityListReader.EntityKind.Shrine;
        if (metadata.StartsWith("Metadata/Effects/", StringComparison.OrdinalIgnoreCase)) return EntityListReader.EntityKind.Effect;
        return EntityListReader.EntityKind.Unknown;
    }
}
