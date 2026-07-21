using BubblesBot.Core.Game;

namespace BubblesBot.Core.Campaign;

/// <summary>
/// Reads the current area's raw id (e.g. <c>"1_1_2"</c>) — the key that joins the live game to the
/// campaign route and target catalog. Validated live 2026-07-17 via POEMCP: the pointer at
/// <see cref="KnownOffsets.IngameData.CurrentArea"/> (+0xA8) is the AreaTemplate, whose
/// <see cref="KnownOffsets.WorldArea.Id"/> (+0x0) is a char* to the raw id.
///
/// <para>The primary read uses that committed chain. A candidate-offset scan is kept as a drift
/// fallback that accepts a string only if it matches a <b>known route area id</b>, so a layout shift
/// degrades to "unknown area" (guidance shows a diagnostic) rather than mis-guiding.</para>
/// </summary>
public static class AreaIdentityReader
{
    // Fallback scan offsets on the AreaTemplate struct (used only if the committed +0x0 read fails).
    private static readonly int[] CandidateOffsets =
        { 0x0, 0x8, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40 };

    public static string CurrentAreaId(MemoryReader reader, nint ingameData, Func<string, bool> isKnownAreaId)
    {
        if (!reader.TryReadStruct<nint>(ingameData + KnownOffsets.IngameData.CurrentArea, out var area) || area == 0)
            return string.Empty;

        // Primary: validated layout — AreaTemplate.Id is a char* at +0x0.
        var id = ReadCStr(reader, area + KnownOffsets.WorldArea.Id);
        if (Plausible(id) && isKnownAreaId(id)) return id;

        // Fallback: scan candidate offsets (char* deref, then NativeString) in case the layout drifts.
        foreach (var off in CandidateOffsets)
        {
            var s = TryReadId(reader, area + off);
            if (s.Length > 0 && isKnownAreaId(s)) return s;
        }
        return string.Empty;
    }

    private static string TryReadId(MemoryReader reader, nint addr)
    {
        // Plain char* (the real layout): one pointer hop → null-terminated UTF-16.
        var s = ReadCStr(reader, addr);
        if (Plausible(s)) return s;
        // NativeString (SSO/length-prefixed) — kept for robustness against layout drift.
        try { var n = NativeString.Read(reader, addr, 48); if (Plausible(n)) return n; } catch { /* bad ptr */ }
        return string.Empty;
    }

    /// <summary>Read a pointer at <paramref name="addr"/> then the null-terminated UTF-16 string it
    /// points to. Empty on any bad read.</summary>
    private static string ReadCStr(MemoryReader reader, nint addr)
    {
        try
        {
            if (reader.TryReadStruct<nint>(addr, out var p) && p != 0)
                return reader.ReadStringUtf16(p, 48);
        }
        catch { /* bad ptr */ }
        return string.Empty;
    }

    private static bool Plausible(string s)
        => s.Length is > 0 and <= 32 && s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
