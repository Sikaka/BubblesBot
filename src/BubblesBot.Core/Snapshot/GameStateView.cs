using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>Classification of PoE's active top-level state, from <see cref="GameStateView.ReadKind"/>.</summary>
public enum GameStateKind
{
    /// <summary>No slot addresses supplied — the gate is off and the bot acts as if in game.</summary>
    GateDisabled,
    /// <summary>World is live; reads and inputs are safe.</summary>
    InGame,
    /// <summary>
    /// The game-root container is torn down or being rebuilt (global slots null / chain
    /// unresolvable). Observed for up to several seconds during area transitions — this is
    /// the primary loading-gate signal.
    /// </summary>
    Transition,
    /// <summary>The AreaLoadingState object is the active state.</summary>
    Loading,
    /// <summary>The login screen is the active state.</summary>
    Login,
    /// <summary>A resolvable state we don't classify (character select, credits, ...).</summary>
    Other,
}

/// <summary>
/// Read-only view of PoE's top-level game-state machine. Tells the bot whether the world
/// is currently live (in a loaded zone) vs. transitioning, loading, or at login — so
/// dispatch logic can gate "read player + entity data" behind a known-safe signal instead
/// of guessing from null pointers.
///
/// <para><b>How it works.</b> The game-root container embedding TheGame is destroyed and
/// reallocated on every area transition (observed live 2026-07-13), so nothing here is
/// cached: every read re-follows image slot → container → +0xA00 → TheGame via
/// <see cref="TheGameResolver.TryReadLiveTheGame"/>, then compares
/// <c>TheGame.CurrentStatePtr</c> to the state slots. An unresolvable chain is reported as
/// <see cref="GameStateKind.Transition"/> — the slots sit at NULL for seconds mid-load,
/// which is precisely the window the bot must not act in.</para>
///
/// <para><b>Failure semantics.</b> The chain resolution fails CLOSED (not-live) — a null
/// slot is a real transition signal, and image-global reads don't fail transiently. Only a
/// missing slot list (gate not adopted / patterns absent) fails open via
/// <see cref="GameStateKind.GateDisabled"/>.</para>
///
/// <para><b>State classification caveat.</b> Inactive state slots hold obfuscated garbage
/// in current builds, so Loading/Login labels are best-effort: the gate's correctness only
/// depends on "CurrentStatePtr == IngameState slot value", which is stable.</para>
/// </summary>
public sealed class GameStateView
{
    private readonly MemoryReader _reader;
    private readonly IReadOnlyList<nint> _slots;

    public GameStateView(MemoryReader reader, IReadOnlyList<nint> slotAddresses)
    {
        _reader = reader;
        _slots = slotAddresses;
    }

    /// <summary>Convenience for diagnostics: the slot addresses this view follows.</summary>
    public IReadOnlyList<nint> SlotAddresses => _slots;

    /// <summary>
    /// One-shot classification of the active game state. This is what the bot's tick loop
    /// gates on and what telemetry reports.
    /// </summary>
    public GameStateKind ReadKind()
    {
        if (_slots.Count == 0) return GameStateKind.GateDisabled;

        var theGame = TheGameResolver.TryReadLiveTheGame(_reader, _slots);
        if (theGame == 0) return GameStateKind.Transition;

        if (!_reader.TryReadStruct<nint>(theGame + KnownOffsets.TheGame.CurrentStatePtr, out var current) || current == 0)
            return GameStateKind.Transition;

        if (_reader.TryReadStruct<nint>(theGame + KnownOffsets.TheGame.IngameState, out var ingame)
            && ingame != 0 && current == ingame)
            return GameStateKind.InGame;
        if (_reader.TryReadStruct<nint>(theGame + KnownOffsets.TheGame.LoadingState, out var loading)
            && loading != 0 && current == loading)
            return GameStateKind.Loading;
        if (_reader.TryReadStruct<nint>(theGame + KnownOffsets.TheGame.LoginState, out var login)
            && login != 0 && current == login)
            return GameStateKind.Login;
        return GameStateKind.Other;
    }

    /// <summary>True when the bot can safely read player + entity data and send inputs.</summary>
    public bool IsInGameState => ReadKind() is GameStateKind.InGame or GameStateKind.GateDisabled;

    /// <summary>True while the world is torn down or a load screen is up.</summary>
    public bool IsLoading => ReadKind() is GameStateKind.Transition or GameStateKind.Loading;

    /// <summary>True when the login screen is up. Bot should sleep entirely.</summary>
    public bool IsLogin => ReadKind() == GameStateKind.Login;

    /// <summary>Address of the live state object (0 while transitioning). Diagnostic.</summary>
    public nint CurrentStatePointer
    {
        get
        {
            var theGame = TheGameResolver.TryReadLiveTheGame(_reader, _slots);
            if (theGame == 0) return 0;
            return _reader.TryReadStruct<nint>(theGame + KnownOffsets.TheGame.CurrentStatePtr, out var p) ? p : 0;
        }
    }
}
