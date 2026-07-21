using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Reads PoE's blight encounter countdown UI ("M:SS" / "MM:SS" string above the pump). The
/// element is at a fixed UI tree path validated 2026-05-08:
/// <c>IngameUi.Parent.Children[1].Children[25].Children[4].Children[0].Children[0].Children[0].Children[0]</c>.
///
/// <para><b>Why a UI read instead of a state-machine read.</b> The pump's StateMachine
/// <c>success</c>/<c>fail</c> only flips after the encounter has fully resolved (timer +
/// settle window). The UI countdown reaches "0:00" earlier — at that moment no new mobs
/// will spawn, but stragglers may still be alive. That's the right transition point from
/// "defend the pump" to "sweep stragglers." Mirrors AutoExile's <c>BlightState.TrackCountdown</c>.</para>
///
/// <para>The text is stored inline in the Element struct via PoE's small-string-optimized
/// NativeUtf16Text. For typical countdown strings ("0:00", "1:23", "12:34") the text fits
/// in the 7-char inline window so we never have to chase the heap pointer.</para>
/// </summary>
public static class BlightTimerView
{
    private static readonly int[] CountdownPath = { 1, 25, 4, 0, 0, 0, 0 };

    /// <summary>
    /// Returns the raw countdown text (e.g. "1:23"), or null when the encounter UI isn't up.
    /// Empty string is normal between encounters; exact value "0:00" / "00:00" indicates the
    /// timer has elapsed.
    /// </summary>
    public static string? ReadCountdownText(MemoryReader reader, nint ingameStateAddress)
    {
        // IngameUi.Parent — the parent of the IngameUi panel. Lives at IngameUi+0x140 per
        // standard ExileApi layout. Use the Element.Parent walk to keep this loose-coupled
        // (no hardcoded IngameUi-internal offset for "Parent").
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
            return null;
        if (!reader.TryReadStruct<nint>(ingameUi + KnownOffsets.Element.Parent, out var parent) || parent == 0)
            return null;

        var elem = parent;
        foreach (var idx in CountdownPath)
        {
            if (!reader.TryReadStruct<nint>(elem + KnownOffsets.Element.Childs, out var begin) || begin == 0) return null;
            if (!reader.TryReadStruct<nint>(begin + idx * 8, out var child) || child == 0) return null;
            elem = child;
        }

        // Read NativeUtf16Text at Element + 0x380. Length-driven SSO: <=7 chars stored
        // inline (the Buffer field IS the text); >7 chars heap-allocated at the buffer ptr.
        if (!reader.TryReadStruct<NativeUtf16Text>(elem + KnownOffsets.Element.Text, out var t)
            || t.Length < 0 || t.Length > 64)
            return null;

        if (t.Length == 0) return string.Empty;

        var text = t.Length <= 7
            ? reader.ReadStringUtf16(elem + KnownOffsets.Element.Text, (int)t.Length)
            : reader.ReadStringUtf16(t.Buffer, (int)t.Length);
        return text;
    }

    /// <summary>True when the timer text reads "0:00" / "00:00" — encounter timer elapsed.</summary>
    public static bool IsTimerDone(string? countdownText)
    {
        if (string.IsNullOrEmpty(countdownText)) return false;
        var trimmed = countdownText.Trim();
        return trimmed == "0:00" || trimmed == "00:00";
    }
}
