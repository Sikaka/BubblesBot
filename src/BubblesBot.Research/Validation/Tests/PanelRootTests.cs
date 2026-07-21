using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class IngameUiPanelRootsOracleTest : ValidationTest
{
    public override string Name => "IngameUi inventory/stash/map-device panel roots";
    public override string? Group => "Inventory/stash";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            IngameState.IngameUi.InventoryPanel.Address.ToString("X") + "|" +
            IngameState.IngameUi.StashElement.Address.ToString("X") + "|" +
            IngameState.IngameUi.AtlasPanel.Address.ToString("X") + "|" +
            IngameState.IngameUi.MapDeviceWindow.Address.ToString("X")
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 4)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var expected = new[]
        {
            ("InventoryPanel", KnownOffsets.IngameUiElements.InventoryPanel, ParseHex(parts[0])),
            ("StashElement", KnownOffsets.IngameUiElements.StashElement, ParseHex(parts[1])),
            ("AtlasPanel", KnownOffsets.IngameUiElements.AtlasPanel, ParseHex(parts[2])),
        };

        var messages = new List<string>();
        var failures = new List<string>();
        foreach (var (name, offset, expectedAddress) in expected)
        {
            if (!ctx.Reader.TryReadStruct<nint>(ingameUi + offset, out var ours))
                return new TestOutcome.Fail(Name, $"could not read {name} at +0x{offset:X}");
            if (ours != expectedAddress)
            {
                var candidates = FindPointerCandidates(ctx.Reader, ingameUi, expectedAddress);
                failures.Add($"{name} mismatch offset +0x{offset:X}: ours=0x{ours:X}, POEMCP=0x{expectedAddress:X}, candidates=[{string.Join(", ", candidates)}]");
                continue;
            }

            messages.Add($"{name}=0x{ours:X}");
        }

        if (failures.Count > 0)
            return new TestOutcome.Fail(Name, string.Join("; ", failures));

        var atlasPanel = ParseHex(parts[2]);
        var expectedMapDevice = ParseHex(parts[3]);
        if (!ElementReader.TryGetChild(ctx.Reader, atlasPanel, KnownOffsets.IngameUiElements.MapDeviceWindowAtlasPanelChildIndex, out var mapDeviceParent)
            || !ElementReader.TryGetChild(ctx.Reader, mapDeviceParent, KnownOffsets.IngameUiElements.MapDeviceWindowChildIndex, out var mapDevice))
        {
            return new TestOutcome.Fail(Name, "could not follow AtlasPanel child path to MapDeviceWindow");
        }
        if (mapDevice != expectedMapDevice)
            return new TestOutcome.Fail(Name, $"MapDeviceWindow mismatch via AtlasPanel children: ours=0x{mapDevice:X}, POEMCP=0x{expectedMapDevice:X}");

        messages.Add($"MapDeviceWindow=0x{mapDevice:X}");

        return new TestOutcome.Pass(Name, string.Join(", ", messages));
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
        for (var off = 0; off < 0x1200; off += 8)
        {
            if (reader.TryReadStruct<nint>(baseAddress + off, out var candidate) && candidate == expected)
                result.Add($"+0x{off:X}");
        }
        return result;
    }
}
