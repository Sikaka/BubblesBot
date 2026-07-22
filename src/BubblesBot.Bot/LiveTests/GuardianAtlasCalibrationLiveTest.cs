using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Passive one-time collector for the four Shaper Guardian Atlas node indices.</summary>
public sealed class GuardianAtlasCalibrationLiveTest : ILiveTestCase
{
    public string Id => "G-11-guardian-atlas-calibrate";
    public string Name => "Calibrate four Guardian Atlas nodes";
    public string Description => "Waits for manual hovers over Phoenix, Minotaur, Chimera, and Hydra and records each exact Atlas data index without moving the mouse.";
    public string ManualSetup => "Open the Atlas at normal zoom, then hover each of the four Guardian maps for about one second.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, (GuardianWitnessStatus Status, int DataIndex)>(
            StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline && found.Count < GuardianRotationPolicy.Maps.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = context.Snapshot();
            if (!snapshot.AtlasPanel.IsVisible)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            var hover = UiHoverView.Read(snapshot.Reader, snapshot.IngameStateAddress);
            var guardian = GuardianRotationPolicy.ClassifyTooltip(hover.TooltipLines);
            if (guardian.WitnessStatus != GuardianWitnessStatus.Unknown)
            {
                var uiIndex = snapshot.AtlasPanel.AtlasCanvasDirectChildForHover(hover.Element);
                if (uiIndex >= MapDeviceSystem.CurrentAtlasNodeUiPrefix)
                {
                    var dataIndex = uiIndex - MapDeviceSystem.CurrentAtlasNodeUiPrefix;
                    if (!found.ContainsKey(guardian.MapName))
                    {
                        found[guardian.MapName] = (guardian.WitnessStatus, dataIndex);
                        context.Observe(
                            "guardian atlas calibration",
                            $"map='{guardian.MapName}' status={guardian.WitnessStatus} "
                            + $"dataIndex={dataIndex} progress={found.Count}/4");
                    }
                }
            }
            await Task.Delay(100, cancellationToken);
        }

        foreach (var map in GuardianRotationPolicy.Maps)
            context.Check(found.ContainsKey(map), map,
                found.TryGetValue(map, out var value)
                    ? $"status={value.Status} dataIndex={value.DataIndex}"
                    : "not hovered");
        return found.Count == GuardianRotationPolicy.Maps.Count
            ? LiveTestCaseResult.Pass(
                string.Join("; ", GuardianRotationPolicy.Maps.Select(map =>
                    $"{map}={found[map].DataIndex}")),
                "GuardianAtlasCalibrated")
            : LiveTestCaseResult.Blocked(
                $"captured {found.Count}/4 Guardian nodes before timeout",
                "GuardianAtlasCalibrationIncomplete");
    }
}
