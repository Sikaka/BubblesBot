using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Read-only, short capture of The Formed arena podium. Run once before interaction and once
/// immediately after interaction to discover the authoritative readiness/activation signal.
/// </summary>
public sealed class GuardianFormedPodiumInspectLiveTest : ILiveTestCase
{
    private const string PodiumMetadata =
        "Metadata/Terrain/EndGame/MapAtlasMaven/Objects/MavenBossRushObject";

    public string Id => "G-10-formed-podium-inspect";
    public string Name => "The Formed podium state inspect";
    public string Description => "Read-only capture of podium targetability, components, and raw state-machine values.";
    public string ManualSetup => "Stand near the center podium in The Formed arena, either just before or just after clicking it.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var observations = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = context.Snapshot();
            if (!game.Reader.TryReadStruct<nint>(
                    game.IngameDataAddress + KnownOffsets.IngameData.EntityList, out var entityList)
                || entityList == 0)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            var traversal = EntityListReader.EnumerateEntityAddresses(game.Reader, entityList);
            var podium = traversal.EntityAddresses
                .Select(address => EntityListReader.TryReadSnapshot(game.Reader, address))
                .FirstOrDefault(entity => string.Equals(
                    entity?.Metadata, PodiumMetadata, StringComparison.Ordinal));

            if (podium is null)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            podium.Components.TryGetValue("StateMachine", out var stateMachine);
            var states = stateMachine == 0
                ? Array.Empty<long>()
                // This entity currently exposes one named state. Reading beyond the known
                // count walks unrelated adjacent memory and produces pointer-shaped noise.
                : StateMachineView.ReadValues(
                    game.Reader, stateMachine, MavenInvitationStates.BossRushObject.Count);
            podium.Components.TryGetValue("Targetable", out var targetable);
            var isTargeted = targetable != 0
                && game.Reader.TryReadStruct<byte>(
                    targetable + KnownOffsets.TargetableComponent.IsTargeted, out var rawIsTargeted)
                    ? rawIsTargeted != 0
                    : (bool?)null;
            var signature =
                $"targetable={Format(podium.IsTargetable)} " +
                $"isTargeted={Format(isTargeted)} " +
                $"canBeTarget={Format(podium.StateMachine?.CanBeTarget)} " +
                $"inTarget={Format(podium.StateMachine?.InTarget)} " +
                $"states=[{string.Join(",", states.Select((value, index) => $"{index}:{value}"))}]";

            if (signatures.Add(signature))
            {
                observations++;
                var grid = podium.GridPosition is { } position
                    ? $"({position.X},{position.Y})"
                    : "unknown";
                context.Observe("Formed podium state", $"id={podium.Id} grid={grid} {signature}");
                context.Observe("Formed podium components",
                    string.Join(",", podium.Components.Keys.Order(StringComparer.Ordinal)));
            }

            await Task.Delay(100, cancellationToken);
        }

        context.Check(observations > 0, "Formed podium found",
            observations > 0
                ? $"captured {observations} distinct state signature(s)"
                : "center MavenBossRushObject was not visible in the entity list");

        return observations > 0
            ? LiveTestCaseResult.Pass(
                $"captured {observations} distinct podium state signature(s)",
                "ReadOnlyStateCapture")
            : LiveTestCaseResult.Blocked(
                "The Formed podium was not visible; stand in its network bubble and retry",
                "PodiumNotFound");
    }

    private static string Format(bool? value)
        => value?.ToString() ?? "unknown";
}
