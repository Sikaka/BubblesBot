using System.Buffers;
using System.Text;
using BubblesBot.Core;

namespace BubblesBot.Research.Probing.Toolkit;

/// <summary>
/// Oracle-free scanning primitives — the engine behind a probe's Discover path. Two families:
///   - Window* : scan a fixed window starting at a known base, return matching OFFSETS (relative
///               to the base) — for "the field moved within this struct".
///   - Regions*: scan all private committed regions, return absolute ADDRESSES — for value/string
///               search and reference-finding ("what points at X?").
/// </summary>
public static class MemScan
{
    // ---- Window scans (return offsets relative to baseAddr) ----

    public static List<int> WindowInt32(MemoryReader r, nint baseAddr, int window, int value)
    {
        var hits = new List<int>();
        var buf = new byte[window];
        var n = r.TryReadBytes(baseAddr, buf);
        for (var o = 0; o + 4 <= n; o += 4)
            if (BitConverter.ToInt32(buf, o) == value) hits.Add(o);
        return hits;
    }

    public static List<int> WindowFloat(MemoryReader r, nint baseAddr, int window, float value, float epsilon = 0.01f)
    {
        var hits = new List<int>();
        var buf = new byte[window];
        var n = r.TryReadBytes(baseAddr, buf);
        for (var o = 0; o + 4 <= n; o += 4)
        {
            var f = BitConverter.ToSingle(buf, o);
            if (float.IsFinite(f) && Math.Abs(f - value) <= epsilon) hits.Add(o);
        }
        return hits;
    }

    public static List<int> WindowPtr(MemoryReader r, nint baseAddr, int window, nint value)
    {
        var hits = new List<int>();
        var buf = new byte[window];
        var n = r.TryReadBytes(baseAddr, buf);
        for (var o = 0; o + 8 <= n; o += 8)
            if ((nint)BitConverter.ToInt64(buf, o) == value) hits.Add(o);
        return hits;
    }

    // ---- Region scans (return absolute addresses) ----

    public static List<nint> RegionsInt32(MemoryReader r, int value, int max = 200)
        => Regions(r, BitConverter.GetBytes(value), align: 4, max);

    public static List<nint> RegionsFloat(MemoryReader r, float value, int max = 200)
        => Regions(r, BitConverter.GetBytes(value), align: 4, max);

    /// <summary>Find 8-byte-aligned slots whose value equals <paramref name="value"/> — i.e. references TO it.</summary>
    public static List<nint> RegionsRefsTo(MemoryReader r, nint value, int max = 200)
        => Regions(r, BitConverter.GetBytes((long)value), align: 8, max);

    public static List<nint> RegionsUtf16(MemoryReader r, string text, int max = 200)
        => Regions(r, Encoding.Unicode.GetBytes(text), align: 2, max);

    public static List<nint> RegionsUtf8(MemoryReader r, string text, int max = 200)
        => Regions(r, Encoding.UTF8.GetBytes(text), align: 1, max);

    /// <summary>Generic needle scan over private committed regions.</summary>
    public static List<nint> Regions(MemoryReader r, byte[] needle, int align, int max)
    {
        var hits = new List<nint>();
        if (needle.Length == 0) return hits;

        var pool = ArrayPool<byte>.Shared;
        const int chunkSize = 1 * 1024 * 1024;
        var buf = pool.Rent(chunkSize);
        try
        {
            foreach (var (regionBase, regionSize) in r.Process.EnumerateReadableRegions(privateOnly: true))
            {
                long offset = 0;
                while (offset < regionSize)
                {
                    var toRead = (int)Math.Min(chunkSize, regionSize - offset);
                    var span = buf.AsSpan(0, toRead);
                    var read = r.TryReadBytes(regionBase + (nint)offset, span);
                    if (read == 0) break;
                    var usable = read < toRead ? read : toRead;

                    for (var i = 0; i + needle.Length <= usable; i += align)
                    {
                        if (!span.Slice(i, needle.Length).SequenceEqual(needle)) continue;
                        hits.Add(regionBase + (nint)(offset + i));
                        if (hits.Count >= max) return hits;
                    }

                    if (read < toRead) break;
                    offset += toRead;
                }
            }
        }
        finally
        {
            pool.Return(buf);
        }
        return hits;
    }
}
