using System.Text;
using BubblesBot.Core;

namespace BubblesBot.Research.Probing.Toolkit;

/// <summary>
/// Renders a memory window as 8-byte rows, each slot interpreted several ways at once
/// (hex bytes / i64 / 2x i32 / 2x f32 / pointer? / ascii) — the manual-inspection view you
/// reach for when a probe's automated discovery comes up empty. Pure formatting over
/// <see cref="MemoryReader"/>; no game knowledge.
/// </summary>
public static class MemDump
{
    public static string Window(MemoryReader r, nint baseAddr, int length)
    {
        length = Math.Clamp(length, 0x10, 0x4000);
        var buf = new byte[length];
        var n = r.TryReadBytes(baseAddr, buf);
        var sb = new StringBuilder();
        sb.AppendLine($"0x{(long)baseAddr:X}  ({n} bytes readable)");
        sb.AppendLine("  off    bytes                     i64                 i32,i32           f32,f32            ascii");
        for (var o = 0; o + 8 <= n; o += 8)
        {
            var i64 = BitConverter.ToInt64(buf, o);
            var i0 = BitConverter.ToInt32(buf, o);
            var i1 = BitConverter.ToInt32(buf, o + 4);
            var f0 = BitConverter.ToSingle(buf, o);
            var f1 = BitConverter.ToSingle(buf, o + 4);
            var ptr = (ulong)i64 is >= 0x10000 and <= 0x7FFFFFFFFFFF ? $"->0x{i64:X}" : "";
            sb.Append($"  +{o:X3}  {Hex(buf, o)}  {i64,18}  {i0,8},{i1,-8}  {Fmt(f0)},{Fmt(f1)}  {Ascii(buf, o)} {ptr}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Hex(byte[] b, int o)
        => string.Join(' ', Enumerable.Range(0, 8).Select(j => b[o + j].ToString("X2")));

    private static string Fmt(float f) => float.IsFinite(f) ? $"{f,8:G4}" : "     nan";

    private static string Ascii(byte[] b, int o)
    {
        var c = new char[8];
        for (var k = 0; k < 8; k++) { var v = b[o + k]; c[k] = v is >= 0x20 and < 0x7f ? (char)v : '.'; }
        return new string(c);
    }
}
