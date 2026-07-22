namespace BubblesBot.Core;

/// <summary>
/// PoE NativeString helper. NativeString is a 32-byte struct: 16 bytes of inline storage
/// (SSO — fits ≤7 UTF-16 chars), then length (Int32 at +0x10), then capacity (Int32 at +0x14).
/// When the string is longer than 7 chars, the inline buffer holds a pointer to heap data
/// instead. Reads always go via this helper so the SSO branch is handled in one place.
///
/// <para>Validated 2026-05-06 against a 17-character player name (heap storage),
/// 2026-05-07 against unique-monster RenderName (heap-style for long names).</para>
/// </summary>
public static class NativeString
{
    /// <summary>
    /// Read a NativeString at <paramref name="address"/>. Returns empty string on any
    /// failure or out-of-range length. Uses the length field to bound reads — never reads
    /// past the declared end, which keeps us safe on freshly-freed entity memory.
    /// </summary>
    public static string Read(MemoryReader reader, nint address, int maxChars = 256)
    {
        if (address == 0) return string.Empty;
        if (!reader.TryReadStruct<int>(address + 0x10, out var length) || length <= 0 || length > maxChars)
            return string.Empty;
        try
        {
            // SSO threshold: ≤7 chars fit inline (7 chars × 2 bytes + null = 16 bytes max).
            if (length < 8) return reader.ReadStringUtf16(address, length);
            if (!reader.TryReadStruct<nint>(address, out var heap) || heap == 0) return string.Empty;
            return reader.ReadStringUtf16(heap, length);
        }
        catch
        {
            return string.Empty;
        }
    }
}
