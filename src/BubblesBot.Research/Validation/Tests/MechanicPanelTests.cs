using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class MechanicPanelRootsOracleTest : ValidationTest
{
    public override string Name => "IngameUi mechanic panel roots";
    public override string? Group => "Mechanic panels";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");

        var panels = new (string Name, int Offset)[]
        {
            ("GemLvlUpPanel", KnownOffsets.IngameUiElements.GemLvlUpPanel),
            ("ResurrectPanel", KnownOffsets.IngameUiElements.ResurrectPanel),
            ("RitualWindow", KnownOffsets.IngameUiElements.RitualWindow),
            ("UltimatumPanel", KnownOffsets.IngameUiElements.UltimatumPanel),
            ("LeagueMechanicButtons", KnownOffsets.IngameUiElements.LeagueMechanicButtons),
            ("NpcDialog", KnownOffsets.IngameUiElements.NpcDialog),
            ("CurrencyExchangePanel", KnownOffsets.IngameUiElements.CurrencyExchangePanel),
            ("LabyrinthDivineFontPanel", KnownOffsets.IngameUiElements.LabyrinthDivineFontPanel),
            ("HeistWindow", KnownOffsets.IngameUiElements.HeistWindow),
            ("BlueprintWindow", KnownOffsets.IngameUiElements.BlueprintWindow),
            ("HeistLockerElement", KnownOffsets.IngameUiElements.HeistLockerElement),
            ("ExpeditionWindow", KnownOffsets.IngameUiElements.ExpeditionWindow),
            ("ExpeditionLockerElement", KnownOffsets.IngameUiElements.ExpeditionLockerElement),
            ("ExpeditionDetonatorElement", KnownOffsets.IngameUiElements.ExpeditionDetonatorElement),
            ("SanctumFloorWindow", KnownOffsets.IngameUiElements.SanctumFloorWindow),
            ("SanctumRewardWindow", KnownOffsets.IngameUiElements.SanctumRewardWindow),
            ("GenesisTreeWindow", KnownOffsets.IngameUiElements.GenesisTreeWindow),
            ("BlightEncounter", KnownOffsets.IngameUiElements.BlightEncounterUi),
        };

        var expression = "string.Join(\"|\", new[]{" + string.Join(",", panels.Select(p => $"IngameState.IngameUi.{p.Name}.Address.ToString(\"X\")")) + "})";
        var truth = await ctx.Poemcp.EvalAsync(expression, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != panels.Length)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var failures = new List<string>();
        var nonZero = 0;
        for (var i = 0; i < panels.Length; i++)
        {
            var expected = ParseHex(parts[i]);
            if (!ctx.Reader.TryReadStruct<nint>(ingameUi + panels[i].Offset, out var ours))
            {
                failures.Add($"{panels[i].Name}: read failed at +0x{panels[i].Offset:X}");
                continue;
            }
            if (ours != expected)
                failures.Add($"{panels[i].Name}: ours=0x{ours:X} +0x{panels[i].Offset:X}, POEMCP=0x{expected:X}, candidates=[{string.Join(", ", FindPointerCandidates(ctx.Reader, ingameUi, expected))}]");
            if (expected != 0)
                nonZero++;
        }

        if (failures.Count > 0)
            return new TestOutcome.Fail(Name, string.Join("; ", failures));

        return new TestOutcome.Pass(Name, $"validated={panels.Length}, nonZero={nonZero}");
    }

    private static nint ParseHex(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return (nint)long.Parse(value, System.Globalization.NumberStyles.HexNumber);
    }

    private static IReadOnlyList<string> FindPointerCandidates(MemoryReader reader, nint baseAddress, nint expected)
    {
        var result = new List<string>();
        for (var off = 0; off < 0x1000; off += 8)
        {
            if (reader.TryReadStruct<nint>(baseAddress + off, out var candidate) && candidate == expected)
                result.Add($"+0x{off:X}");
        }
        return result;
    }
}
