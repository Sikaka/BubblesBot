namespace BubblesBot.Core.Game;

/// <summary>
/// Resolves the <c>TheGame</c> root object and validates its shape.
///
/// <para><b>Lifetime — the critical fact (observed live 2026-07-13):</b> the game-root
/// container that embeds TheGame is <b>destroyed and reallocated on every area
/// transition</b>, with the global slots passing through NULL for up to several seconds
/// mid-load. TheGame's address is therefore only valid until the next zone change. Never
/// cache it: cache the image-global <b>slot addresses</b> (stable for the process
/// lifetime), and re-follow slot → container → +0xA00 → TheGame on every read via
/// <see cref="TryReadLiveTheGame"/>. A null/unresolvable chain is not an error — it IS the
/// "world is being rebuilt" signal the gate exists to detect.</para>
///
/// <para><b>Slot trust:</b> three global slots hold the container; two update in lockstep
/// on every transition while the third lags with stale or null values. Reads therefore
/// take the <b>majority non-null container value</b> across all slots rather than trusting
/// any single one. The heap additionally carries stale freed copies and decoys of TheGame
/// that still pass shape checks (IngameState is process-stable, so a corpse's State4 slot
/// keeps matching) — only the slot-reachable object is live.</para>
/// </summary>
public static class TheGameResolver
{
    /// <summary>
    /// Structural check: does <paramref name="candidate"/> look like TheGame, given the
    /// known-good IngameState address? Requires the State4 slot to equal
    /// <paramref name="ingameState"/> and CurrentStatePtr to match one of the state slots.
    /// Note: freed copies also pass this — shape alone cannot prove liveness.
    /// </summary>
    public static bool LooksLikeTheGame(MemoryReader reader, nint candidate, nint ingameState)
    {
        if (candidate == 0 || ingameState == 0) return false;

        if (!reader.TryReadStruct<nint>(candidate + KnownOffsets.TheGame.IngameState, out var igs)
            || igs != ingameState)
            return false;

        if (!reader.TryReadStruct<nint>(candidate + KnownOffsets.TheGame.CurrentStatePtr, out var current)
            || current == 0)
            return false;

        var currentMatchesSlot = false;
        for (var s = 0; s < KnownOffsets.TheGame.StateSlotCount; s++)
        {
            var off = KnownOffsets.TheGame.StateSlot0 + s * KnownOffsets.TheGame.StateSlotStride;
            if (!reader.TryReadStruct<nint>(candidate + off, out var state)) return false;
            if (state != 0 && state == current) currentMatchesSlot = true;
        }
        return currentMatchesSlot;
    }

    /// <summary>
    /// Bootstrap step: AOB-scan for the container global slots. Returns every distinct slot
    /// address the committed patterns resolve (deduplicated, unvalidated). Slot addresses
    /// live in PoE.exe's image and are stable for the process lifetime — these, not TheGame,
    /// are what callers should keep.
    /// </summary>
    public static IReadOnlyList<nint> ResolveSlotsViaAob(ProcessHandle process, MemoryReader reader)
    {
        var slots = new List<nint>();
        foreach (var pattern in AobPatterns.TheGameRefs)
            foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pattern))
                if (slot != 0 && !slots.Contains(slot))
                    slots.Add(slot);
        return slots;
    }

    /// <summary>
    /// Follow the live chain: majority non-null container across <paramref name="slots"/>,
    /// then container → +0xA00 → TheGame. Returns 0 whenever the chain doesn't resolve —
    /// which legitimately happens for seconds at a time during area transitions.
    /// No shape check here (this runs at render rate); shape is asserted once at bootstrap.
    /// </summary>
    public static nint TryReadLiveTheGame(MemoryReader reader, IReadOnlyList<nint> slots)
    {
        var container = ReadMajorityContainer(reader, slots);
        if (container == 0) return 0;
        return reader.TryReadStruct<nint>(container + KnownOffsets.TheGame.ContainerTheGamePtr, out var theGame)
            ? theGame : 0;
    }

    /// <summary>
    /// The container value agreed on by a REAL majority of slots: at least 2 when 2+ slots
    /// are committed. This is load-bearing, not a tie-break nicety — during transitions the
    /// two lockstep slots go NULL while the laggard slot keeps a STALE container whose freed
    /// TheGame corpse still reads "InGame" (IngameState's address is process-stable, so the
    /// corpse keeps passing every shape comparison). A single non-null vote must therefore
    /// resolve to 0 (= Transition), never to the laggard's value.
    /// </summary>
    public static nint ReadMajorityContainer(MemoryReader reader, IReadOnlyList<nint> slots)
    {
        Span<nint> values = stackalloc nint[8];
        Span<int> votes = stackalloc int[8];
        var n = 0;

        foreach (var slot in slots)
        {
            if (!reader.TryReadStruct<nint>(slot, out var v) || v == 0) continue;
            var found = false;
            for (var i = 0; i < n; i++)
                if (values[i] == v) { votes[i]++; found = true; break; }
            if (!found && n < values.Length) { values[n] = v; votes[n] = 1; n++; }
        }

        var required = Math.Min(2, slots.Count);
        var best = (nint)0; var bestVotes = 0;
        for (var i = 0; i < n; i++)
            if (votes[i] > bestVotes) { best = values[i]; bestVotes = votes[i]; }
        return bestVotes >= required ? best : 0;
    }

    /// <summary>
    /// Bootstrap validation: resolve slots, follow the live chain once, and shape-check the
    /// result against the independently resolved IngameState. Returns the validated slot
    /// list, or empty when nothing resolves (patterns stale — caller fails loud).
    /// </summary>
    public static IReadOnlyList<nint> ResolveAndValidate(ProcessHandle process, MemoryReader reader, nint ingameState)
    {
        var slots = ResolveSlotsViaAob(process, reader);
        if (slots.Count == 0) return [];
        var theGame = TryReadLiveTheGame(reader, slots);
        return theGame != 0 && LooksLikeTheGame(reader, theGame, ingameState) ? slots : [];
    }
}
