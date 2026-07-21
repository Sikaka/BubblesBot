using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Pre-flight read sanity checks. Runs once at bootstrap after IngameData is resolved. If any
/// canary fails, the bot refuses to start — the offset table is stale and self-healing is not
/// our job (run BubblesBot.Research instead).
/// </summary>
public static class CanaryCheck
{
    public sealed record Result(bool Passed, IReadOnlyList<string> Failures);

    public static Result Run(MemoryReader reader, nint ingameDataAddress, nint ingameStateAddress)
    {
        var failures = new List<string>();

        // 1. Player entity resolves and Life looks plausible.
        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.LocalPlayer, out var player) || player == 0)
        {
            failures.Add("LocalPlayer pointer not readable / null");
        }
        else
        {
            var components = EntityComponents.ReadComponentMap(reader, player);
            if (!components.TryGetValue("Life", out var life) || life == 0)
                failures.Add("Player Life component missing");
            else if (!reader.TryReadStruct<VitalStruct>(life + KnownOffsets.LifeComponent.Health, out var v) || !v.LooksValid())
                failures.Add($"Player HP looks invalid (cur={v.Current}, max={v.Max})");

            if (!components.TryGetValue("Positioned", out var positioned) || positioned == 0)
                failures.Add("Player Positioned component missing");
            else if (!reader.TryReadStruct<Vector2i>(positioned + KnownOffsets.PositionedComponent.GridPosition, out var grid)
                     || Math.Abs(grid.X) > 50_000 || Math.Abs(grid.Y) > 50_000)
                failures.Add("Player grid position implausible");
        }

        // 2. Area level in valid PoE1 range.
        if (!reader.TryReadStruct<byte>(ingameDataAddress + KnownOffsets.IngameData.CurrentAreaLevel, out var lvl) || lvl < 1 || lvl > 100)
            failures.Add($"Area level out of range ({lvl})");

        // 3. League string is non-empty (server data offsets are intact).
        if (!reader.TryReadStruct<NativeUtf16Text>(ingameDataAddress + KnownOffsets.IngameData.ServerData, out var serverDataPtr))
        {
            // ServerData is a pointer, not text — the typed read above is just to validate the field exists.
        }
        if (reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.ServerData, out var sd) && sd != 0)
        {
            if (!reader.TryReadStruct<NativeUtf16Text>(sd + KnownOffsets.ServerData.League, out var leagueText)
                || leagueText.Length <= 0 || leagueText.Length > 64)
                failures.Add("ServerData.League text looks invalid");
        }
        else
        {
            failures.Add("ServerData pointer not readable");
        }

        // 4. IngameUi root has children (i.e. UI tree is intact).
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui) || ui == 0)
        {
            failures.Add("IngameUi pointer not readable");
        }
        else if (!reader.TryReadStruct<NativePtrArray>(ui + KnownOffsets.Element.Childs, out var childs)
                 || childs.Count <= 0 || childs.Count > 10_000)
        {
            failures.Add($"IngameUi child count looks invalid ({childs.Count})");
        }

        return new Result(failures.Count == 0, failures);
    }
}
