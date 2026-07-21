using System.Text.Json;
using BubblesBot.Core;

namespace BubblesBot.Research.Probing.Toolkit;

/// <summary>
/// Differential discovery over a struct window: snapshot the bytes at an address, perform some
/// in-game action, then diff to see which offsets changed. The workhorse for finding fields whose
/// value you can toggle live (opened flags, state machines, counters) when no value-scan anchor
/// exists. Snapshots persist under <c>%LOCALAPPDATA%/BubblesBot/research/snapshots</c>.
///
/// <para>Scoped to a single address window (not whole gigabyte page ranges) — that covers the
/// "watch this struct while I do X" workflow without unbounded I/O.</para>
/// </summary>
public static class PageDiff
{
    private static string Dir
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BubblesBot", "research", "snapshots");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string PathFor(string tag) => Path.Combine(Dir, $"{tag}.snap.json");

    public static void Snapshot(MemoryReader r, nint addr, int length, string tag)
    {
        length = Math.Clamp(length, 8, 0x10000);
        var buf = new byte[length];
        var n = r.TryReadBytes(addr, buf);
        var dto = new Snap { Addr = (long)addr, Length = n, BytesHex = Convert.ToHexString(buf, 0, n) };
        File.WriteAllText(PathFor(tag), JsonSerializer.Serialize(dto));
        Console.WriteLine($"snapshot '{tag}': {n} bytes @ 0x{(long)addr:X} -> {PathFor(tag)}");
    }

    /// <summary>Compare a fresh read at the snapshot's address against the saved bytes; print changed offsets.</summary>
    public static void Diff(MemoryReader r, string tag)
    {
        var path = PathFor(tag);
        if (!File.Exists(path)) { Console.Error.WriteLine($"no snapshot '{tag}' (run snapshot first)"); return; }
        var dto = JsonSerializer.Deserialize<Snap>(File.ReadAllText(path));
        if (dto is null) { Console.Error.WriteLine($"snapshot '{tag}' unreadable"); return; }

        var old = Convert.FromHexString(dto.BytesHex);
        var now = new byte[old.Length];
        var n = r.TryReadBytes((nint)dto.Addr, now);
        var limit = Math.Min(n, old.Length);

        var changes = 0;
        Console.WriteLine($"diff '{tag}' @ 0x{dto.Addr:X} ({limit} bytes compared):");
        for (var o = 0; o + 4 <= limit; o += 4)
        {
            var a = BitConverter.ToInt32(old, o);
            var b = BitConverter.ToInt32(now, o);
            if (a == b) continue;
            changes++;
            Console.WriteLine($"  +0x{o:X3}: {a} (0x{a:X}) -> {b} (0x{b:X})");
        }
        Console.WriteLine(changes == 0 ? "  (no 4-byte-aligned changes)" : $"  {changes} changed dword(s)");
    }

    private sealed class Snap
    {
        public long Addr { get; set; }
        public int Length { get; set; }
        public string BytesHex { get; set; } = "";
    }
}
