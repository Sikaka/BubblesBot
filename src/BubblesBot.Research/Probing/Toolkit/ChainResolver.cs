using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Probing.Toolkit;

/// <summary>
/// Resolves IngameState/IngameData with NO dependency on ExileAPI. Tries the committed AOB
/// pattern first (instant when valid); falls back to a value-scan anchored on player HP
/// (<c>--hp N [--mana N] [--es-max N]</c>) and a back-walk to IngameData. This is the same logic
/// the bot's bootstrap uses, kept here so every probe resolves the chain identically.
///
/// <para>(Plan note: this should ultimately be lifted into Core as a single shared resolver used
/// by both the bot and Research. For now it is duplicated here to avoid touching the bot.)</para>
/// </summary>
public static class ChainResolver
{
    public static ResolvedChain? Resolve(ProcessHandle process, MemoryReader reader, string[] args)
    {
        if (TryResolveViaAob(process, reader, out var chain))
            return chain;

        Console.WriteLine("  AOB pattern did not resolve IngameState (stale after a patch?). Trying value-scan...");
        return TryResolveViaValueScan(reader, args);
    }

    private static bool TryResolveViaAob(ProcessHandle process, MemoryReader reader, out ResolvedChain chain)
    {
        chain = null!;
        foreach (var pattern in AobPatterns.IngameStateRefs)
        {
            foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pattern))
            {
                if (!reader.TryReadStruct<nint>(slot, out var igs)) continue;
                if (!reader.TryReadStruct<nint>(igs + KnownOffsets.IngameState.Data, out var igd)) continue;
                if (!reader.TryReadStruct<nint>(igd + KnownOffsets.IngameData.IngameStatePtr, out var roundtrip)) continue;
                if (roundtrip != igs) continue;
                chain = new ResolvedChain(reader, igs, igd, "AOB");
                return true;
            }
        }
        return false;
    }

    private static ResolvedChain? TryResolveViaValueScan(MemoryReader reader, string[] args)
    {
        var hp = GetInt(args, "--hp");
        if (hp is null)
        {
            Console.Error.WriteLine("  No --hp <currentHp> given; cannot value-scan. Pass --hp (and ideally --mana, --es-max).");
            return null;
        }

        var mana = GetInt(args, "--mana");
        var esMax = GetInt(args, "--es-max");
        Console.WriteLine($"  Value-scanning for Life component (hp={hp}{(mana is null ? "" : $", mana={mana}")})...");

        var matches = LifeValidator.FindCandidates(reader, hp.Value, mana, esMax);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine("  No Life match. HP must equal the live value at scan time; stand still and retry.");
            return null;
        }

        var owner = matches[0].OwnerAddress;
        var hits = AnchorBackWalker.FindIngameDataFromPlayer(reader, owner);
        if (hits.Count == 0)
        {
            Console.Error.WriteLine("  Found Life but could not back-walk to IngameData.");
            return null;
        }

        var igd = hits[0].IngameDataAddress;
        var igs = reader.TryReadStruct<nint>(igd + KnownOffsets.IngameData.IngameStatePtr, out var s) ? s : 0;
        return new ResolvedChain(reader, igs, igd, "value-scan");
    }

    private static int? GetInt(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length) return null;
        return int.TryParse(args[i + 1], out var v) ? v : null;
    }
}
