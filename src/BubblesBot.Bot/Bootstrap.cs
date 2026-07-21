using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot;

/// <summary>
/// Resolves IngameState/IngameData/TheGame on startup. Tries AOB scanning first; falls back
/// to a value-scan anchor (--hp N) for development when AOB patterns aren't populated yet.
/// TheGame (the game-state gate) resolves strictly via AOB — if its patterns are committed
/// but stale, startup fails loud rather than running with the gate silently disabled.
/// </summary>
internal static class Bootstrap
{
    public static (nint IngameData, nint IngameState, IReadOnlyList<nint> TheGameSlots) ResolveIngameData(
        ProcessHandle process, MemoryReader reader, string[] args)
    {
        // 1. AOB scan if patterns are stored
        if (AobPatterns.IngameStateRefs.Length > 0)
        {
            Console.WriteLine("Scanning for IngameState via AOB patterns...");
            foreach (var pattern in AobPatterns.IngameStateRefs)
            {
                var slotAddresses = AobScanner.ScanForResolvedAddresses(process, reader, pattern);
                foreach (var slotAddr in slotAddresses)
                {
                    if (!reader.TryReadStruct<nint>(slotAddr, out var candidateIs)) continue;
                    if (!reader.TryReadStruct<nint>(candidateIs + KnownOffsets.IngameState.Data, out var candidateData)) continue;
                    if (!reader.TryReadStruct<nint>(candidateData + KnownOffsets.IngameData.IngameStatePtr, out var roundtrip)) continue;
                    if (roundtrip != candidateIs) continue;

                    Console.WriteLine($"  IngameState via AOB: 0x{candidateIs:X16}");
                    Console.WriteLine($"  IngameData:          0x{candidateData:X16}");
                    return ResolveTheGame(process, reader, candidateData, candidateIs);
                }
            }
        }

        // 2. Value-scan fallback
        var hpArg = TryGetIntArg(args, "--hp");
        if (hpArg is null)
        {
            Console.Error.WriteLine("No AOB patterns stored and no --hp argument provided.");
            Console.Error.WriteLine("Usage: BubblesBot.Bot --hp <currentHp> [--mana <currentMana>]");
            Console.Error.WriteLine("       Or run BubblesBot.Research --discover-aob to populate AOB patterns.");
            return (0, 0, []);
        }

        var manaArg = TryGetIntArg(args, "--mana");
        Console.WriteLine($"Value-scanning for LifeComponent (hp={hpArg}{(manaArg.HasValue ? $", mana={manaArg}" : "")})...");

        var matches = LifeValidator.FindCandidates(reader, hpArg.Value, manaArg,
            onProgress: p =>
            {
                if (p.RegionsScanned % 20 == 0 || p.RegionsScanned == p.TotalRegions)
                    Console.Write($"\r  {p.RegionsScanned}/{p.TotalRegions} regions  {p.BytesScanned / 1024 / 1024} MB  {p.CandidatesFound} hit(s)   ");
            });
        Console.WriteLine();

        if (matches.Count == 0)
        {
            Console.Error.WriteLine("No match. HP must equal current value at scan time; stand still in town.");
            return (0, 0, []);
        }

        var ownerAddr = matches[0].OwnerAddress;
        Console.Write("Back-walking from player entity to IngameData...");
        var anchorHits = AnchorBackWalker.FindIngameDataFromPlayer(reader, ownerAddr);
        if (anchorHits.Count == 0)
        {
            Console.Error.WriteLine("\nCould not locate IngameData from anchor.");
            return (0, 0, []);
        }

        var ingameData  = anchorHits[0].IngameDataAddress;
        var ingameState = reader.TryReadStruct<nint>(ingameData + KnownOffsets.IngameData.IngameStatePtr, out var s) ? s : 0;
        Console.WriteLine($" OK  IngameData: 0x{ingameData:X16}");
        return ResolveTheGame(process, reader, ingameData, ingameState);
    }

    /// <summary>
    /// Final bootstrap step: resolve the game-state gate's slot addresses. Three outcomes:
    /// no patterns committed → gate disabled (loud warning, bot still runs);
    /// patterns resolve + live chain shape-checks → gate active;
    /// patterns committed but stale → fail the whole bootstrap (stale offsets never run silently).
    /// Note the SLOTS are what we keep — TheGame itself is reallocated on every zone change,
    /// so GameStateView re-follows the chain per read.
    /// </summary>
    private static (nint, nint, IReadOnlyList<nint>) ResolveTheGame(
        ProcessHandle process, MemoryReader reader, nint ingameData, nint ingameState)
    {
        if (AobPatterns.TheGameRefs.Length == 0)
        {
            Console.WriteLine("  TheGame: no AOB patterns committed — game-state gate DISABLED.");
            return (ingameData, ingameState, []);
        }

        var slots = TheGameResolver.ResolveAndValidate(process, reader, ingameState);
        if (slots.Count == 0)
        {
            Console.Error.WriteLine("TheGame AOB patterns are stale — refusing to run with the game-state gate broken.");
            Console.Error.WriteLine("Run BubblesBot.Research --discover-thegame and update AobPatterns.TheGameRefs.");
            return (0, 0, []);
        }

        Console.WriteLine($"  TheGame gate: {slots.Count} container slot(s), live TheGame 0x{TheGameResolver.TryReadLiveTheGame(reader, slots):X16}");
        return (ingameData, ingameState, slots);
    }

    private static int? TryGetIntArg(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        if (idx < 0 || idx + 1 >= args.Length) return null;
        return int.TryParse(args[idx + 1], out var v) ? v : null;
    }
}
