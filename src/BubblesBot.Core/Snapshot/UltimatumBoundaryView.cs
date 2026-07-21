using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Reads PoE's "return to area" boundary-warning UI — the floating top-screen banner with
/// a countdown that appears when the player crosses outside the Ultimatum encounter arena.
/// <see cref="IsOutsideArena"/> is the canonical "the game thinks I'm out of bounds" signal
/// the bot uses to both react in real time (walk back) and empirically calibrate the
/// encounter's actual radius (record the player's distance from the spawner the moment
/// the warning hides on the inside-bound transition).
///
/// <para><b>Path (validated 2026-05-18):</b>
/// <c>IngameUi + 0x758</c> → container element (matches <c>IngameUi.Children[25]</c>) →
/// <c>.Children[4].Children[0].Children[8]</c> → the 243×133 warning box → countdown text
/// at <c>.Children[0].Children[0]</c>.</para>
///
/// <para><b>Visibility semantics:</b> when the player is INSIDE, both the warning box and
/// its text child have <c>IsVisible=false</c> — even though the text field memory still
/// holds the last "MM:SS" string. Always gate on <see cref="IsOutsideArena"/>, not text
/// presence.</para>
/// </summary>
public sealed class UltimatumBoundaryView
{
    private readonly MemoryReader _reader;
    private readonly nint _warningElementAddress;

    public bool IsValid => _warningElementAddress != 0;

    /// <summary>
    /// True when the warning panel is visible — i.e. the player is currently outside the
    /// encounter boundary and the game is counting down to fail.
    /// </summary>
    public bool IsOutsideArena
        => _warningElementAddress != 0 && IsElementVisible(_reader, _warningElementAddress);

    /// <summary>
    /// Raw countdown text (e.g. <c>"00:04"</c>). Don't trust this alone — the field persists
    /// after the warning hides. Use only when <see cref="IsOutsideArena"/> is true.
    /// </summary>
    public string CountdownText
    {
        get
        {
            if (_warningElementAddress == 0) return string.Empty;
            var c0 = ChildAddress(_reader, _warningElementAddress, 0);
            if (c0 == 0) return string.Empty;
            var c00 = ChildAddress(_reader, c0, 0);
            if (c00 == 0) return string.Empty;
            return NativeString.Read(_reader, c00 + KnownOffsets.Element.Text);
        }
    }

    private UltimatumBoundaryView(MemoryReader reader, nint warningAddress)
    {
        _reader = reader;
        _warningElementAddress = warningAddress;
    }

    /// <summary>
    /// Resolve via <c>IngameState → IngameUi → AreaBoundaryWarningParent → Children path</c>.
    /// Returns an invalid view when the IngameUi pointer can't be followed.
    /// </summary>
    public static UltimatumBoundaryView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
            return new UltimatumBoundaryView(reader, 0);
        if (!reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.AreaBoundaryWarningParent, out var parent) || parent == 0)
            return new UltimatumBoundaryView(reader, 0);
        // /25/4/0/8 — the warning box. The countdown text sits one more level deeper at /0/0.
        var c4 = ChildAddress(reader, parent, 4);
        if (c4 == 0) return new UltimatumBoundaryView(reader, 0);
        var c40 = ChildAddress(reader, c4, 0);
        if (c40 == 0) return new UltimatumBoundaryView(reader, 0);
        var warn = ChildAddress(reader, c40, 8);
        return new UltimatumBoundaryView(reader, warn);
    }

    // ── Internal helpers ────────────────────────────────────────────────

    private static nint ChildAddress(MemoryReader reader, nint parent, int index)
    {
        if (!reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs, out var begin) || begin == 0) return 0;
        if (!reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs + 8, out var end)) return 0;
        var count = (int)(((long)end - (long)begin) / 8);
        if (index < 0 || index >= count) return 0;
        if (!reader.TryReadStruct<nint>(begin + index * 8, out var ptr)) return 0;
        return ptr;
    }

    private static bool IsElementVisible(MemoryReader reader, nint elementAddress)
        => BubblesBot.Core.Game.ElementReader.IsVisibleDeep(reader, elementAddress);
}
