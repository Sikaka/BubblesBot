using System.Text;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Single destructive-in-instance door click with component and terrain before/after evidence.</summary>
public sealed class MapDoorOpenLiveTest : ILiveTestCase
{
    public string Id => "map-door-open";
    public string Name => "Open one map door and diff terrain";
    public string Description => "Clicks the nearest visible closed Door once, verifies TriggerableBlockage opens, and captures component/terrain changes.";
    public string ManualSetup => "Stand beside the same closed map door with PoE foreground and its Door label visible.";
    public LiveTestMutation Mutation => LiveTestMutation.Irreversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context, CancellationToken cancellationToken)
    {
        var beforeSnapshot = context.Snapshot();
        var player = beforeSnapshot.Player;
        if (player is null)
            return LiveTestCaseResult.Blocked("player unavailable", "PlayerMissing");

        var door = beforeSnapshot.GroundLabels
            .Where(MapDoorInspectLiveTest.IsDoor)
            .Where(label => label.EntityGridPosition is not null)
            .OrderBy(label => label.DistanceToPlayer)
            .FirstOrDefault();
        if (door is null || door.DistanceToPlayer > 20)
            return LiveTestCaseResult.Blocked(
                "no visible closed Door label within 20 grid", "DoorMissing");

        var doorGrid = door.EntityGridPosition!.Value;
        var components = EntityComponents.ReadComponentMap(context.Reader, door.ItemEntityAddress);
        if (!components.TryGetValue("TriggerableBlockage", out var blockage) || blockage == 0)
            return LiveTestCaseResult.Blocked(
                "nearest Door has no TriggerableBlockage component", "BlockageMissing");

        var beforeClosed = ReadByte(context, blockage,
            KnownOffsets.TriggerableBlockageComponent.IsClosed);
        var beforeComponents = CaptureComponents(context, components);
        var beforeTerrain = DoorTerrainCapture.Capture(
            beforeSnapshot, player.GridPosition, doorGrid, radius: 45);
        File.WriteAllText(
            System.IO.Path.Combine(context.EvidenceDirectory, "door-terrain-before.txt"),
            beforeTerrain.Text);
        context.Observe("door before", DescribeState(
            beforeClosed, components, blockage, beforeTerrain),
            StateData(context, components, blockage, beforeClosed, beforeTerrain));
        if (!context.Check(beforeClosed == 1, "door initially closed",
                $"TriggerableBlockage+0x30={beforeClosed?.ToString() ?? "unreadable"}"))
            return LiveTestCaseResult.Blocked(
                "door did not positively report closed before input", "DoorNotClosed");

        if (door.LabelRect is not { } rect)
            return LiveTestCaseResult.Blocked("Door label rectangle unavailable", "DoorRectMissing");
        var avoid = beforeSnapshot.GroundLabels
            .Where(label => label.LabelAddress != door.LabelAddress && label.IsLabelVisible)
            .Select(label => label.LabelRect)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.Value)
            .ToArray();
        if (InteractSystem.FindUncoveredPoint(rect, avoid) is not { } point)
            return LiveTestCaseResult.Blocked("Door label is fully covered", "DoorLabelCovered");
        var (screenX, screenY) = context.Window.ToScreen((int)point.X, (int)point.Y);

        var outcome = await context.VerifiedClickAsync(
            screenX, screenY, ClickIntent.InteractWorld,
            $"open map door id={door.EntityId}",
            postcondition: () => ReadByte(context, blockage,
                KnownOffsets.TriggerableBlockageComponent.IsClosed) == 0,
            timeoutMs: 3000,
            cancellationToken);
        if (outcome != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail(
                $"door click outcome was {outcome}", "DoorOpenUnconfirmed");

        await Task.Delay(500, cancellationToken);
        var afterSnapshot = context.Snapshot();
        var afterPlayer = afterSnapshot.Player;
        if (afterPlayer is null)
            return LiveTestCaseResult.Fail("player unavailable after door click", "PlayerMissingAfterClick");
        var afterClosed = ReadByte(context, blockage,
            KnownOffsets.TriggerableBlockageComponent.IsClosed);
        var afterComponents = CaptureComponents(context, components);
        var afterTerrain = DoorTerrainCapture.Capture(
            afterSnapshot, afterPlayer.GridPosition, doorGrid, radius: 45);
        File.WriteAllText(
            System.IO.Path.Combine(context.EvidenceDirectory, "door-terrain-after.txt"),
            afterTerrain.Text);
        var diff = DiffComponents(beforeComponents, afterComponents);
        var diffPath = System.IO.Path.Combine(
            context.EvidenceDirectory, "door-components-diff.txt");
        File.WriteAllText(diffPath, diff);

        context.Observe("door after", DescribeState(
            afterClosed, components, blockage, afterTerrain),
            StateData(context, components, blockage, afterClosed, afterTerrain));
        context.Observe("component diff", diff.Length == 0 ? "no byte changes" : diff,
            new Dictionary<string, object?> { ["artifact"] = diffPath });

        var labelGone = !afterSnapshot.GroundLabels.Any(label =>
            label.EntityId == door.EntityId && MapDoorInspectLiveTest.IsDoor(label));
        var ok = true;
        ok &= context.Check(afterClosed == 0, "door reports open",
            $"TriggerableBlockage+0x30={afterClosed?.ToString() ?? "unreadable"}");
        ok &= context.Check(labelGone, "closed Door label removed",
            labelGone ? "removed" : "still visible");
        ok &= context.Check(afterTerrain.BehindPathReachable,
            "movement terrain reaches behind door",
            $"before={beforeTerrain.BehindPathReachable} after={afterTerrain.BehindPathReachable}");
        context.Check(afterTerrain.PathReachableCells > beforeTerrain.PathReachableCells,
            "movement component expanded",
            $"before={beforeTerrain.PathReachableCells} after={afterTerrain.PathReachableCells}");

        return ok
            ? LiveTestCaseResult.Pass(
                "door open state and movement-terrain expansion confirmed", "DoorTerrainChanged")
            : LiveTestCaseResult.Fail(
                "door opened but one or more terrain postconditions failed", "DoorTerrainMismatch");
    }

    private static byte? ReadByte(LiveTestContext context, nint component, int offset)
        => context.Reader.TryReadStruct<byte>(component + offset, out var value) ? value : null;

    private static Dictionary<string, byte[]> CaptureComponents(
        LiveTestContext context, IReadOnlyDictionary<string, nint> components)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in new[] { "TriggerableBlockage", "Transitionable", "Targetable", "InteractionAction" })
        {
            if (!components.TryGetValue(name, out var address) || address == 0) continue;
            var length = name == "Transitionable" ? 0x140 : 0x80;
            var bytes = new byte[length];
            if (context.Reader.TryReadBytes(address, bytes) == bytes.Length)
                result[name] = bytes;
        }
        return result;
    }

    private static string DiffComponents(
        IReadOnlyDictionary<string, byte[]> before,
        IReadOnlyDictionary<string, byte[]> after)
    {
        var sb = new StringBuilder();
        foreach (var name in before.Keys.OrderBy(x => x))
        {
            if (!after.TryGetValue(name, out var right)) continue;
            var left = before[name];
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
                if (left[i] != right[i])
                    sb.AppendLine($"{name}+0x{i:X}: 0x{left[i]:X2} -> 0x{right[i]:X2}");
        }
        return sb.ToString();
    }

    private static string DescribeState(
        byte? closed,
        IReadOnlyDictionary<string, nint> components,
        nint blockage,
        DoorTerrainCapture terrain)
    {
        var transition1 = components.TryGetValue("Transitionable", out var transition)
            ? $"{KnownOffsets.TransitionableComponent.Flag1:X}:?"
            : "missing";
        return $"closed={closed?.ToString() ?? "?"} blockage=0x{(long)blockage:X} "
               + $"transition={transition1} {terrain.Summary}";
    }

    private static Dictionary<string, object?> StateData(
        LiveTestContext context,
        IReadOnlyDictionary<string, nint> components,
        nint blockage,
        byte? closed,
        DoorTerrainCapture terrain)
    {
        components.TryGetValue("Transitionable", out var transition);
        return new Dictionary<string, object?>
        {
            ["closed"] = closed,
            ["blockageAddress"] = $"0x{(long)blockage:X}",
            ["blockageMinX"] = ReadInt(context, blockage, KnownOffsets.TriggerableBlockageComponent.MinX),
            ["blockageMinY"] = ReadInt(context, blockage, KnownOffsets.TriggerableBlockageComponent.MinY),
            ["blockageMaxX"] = ReadInt(context, blockage, KnownOffsets.TriggerableBlockageComponent.MaxX),
            ["blockageMaxY"] = ReadInt(context, blockage, KnownOffsets.TriggerableBlockageComponent.MaxY),
            ["transitionFlag1"] = transition == 0 ? null : ReadByte(context, transition, KnownOffsets.TransitionableComponent.Flag1),
            ["transitionFlag2"] = transition == 0 ? null : ReadByte(context, transition, KnownOffsets.TransitionableComponent.Flag2),
            ["pathReachableCells"] = terrain.PathReachableCells,
            ["targetingReachableCells"] = terrain.TargetingReachableCells,
            ["behindPathReachable"] = terrain.BehindPathReachable,
            ["behindTargetingReachable"] = terrain.BehindTargetingReachable,
        };
    }

    private static int? ReadInt(LiveTestContext context, nint component, int offset)
        => context.Reader.TryReadStruct<int>(component + offset, out var value) ? value : null;
}
