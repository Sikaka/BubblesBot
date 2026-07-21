using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Modes;

public enum BlightTowerKind
{
    Chilling,
    Seismic,
    Empowering,
    Meteor,
    Summoning,
    Damage,
}

public readonly record struct BlightTowerOption(BlightTowerKind Kind, int Cost);
public sealed record BlightTowerPad(
    uint EntityId,
    Vector2i Position,
    bool Occupied,
    IReadOnlyList<BlightTowerOption> Options);
public readonly record struct BlightTowerChoice(
    uint EntityId,
    BlightTowerOption Option,
    float Score,
    string Reason);

/// <summary>
/// Pure optional tower policy. The live adapter is responsible only for producing pads/options
/// from validated UI state and confirming the selected build postcondition.
/// </summary>
public static class BlightTowerPolicy
{
    public static BlightTowerChoice? Choose(
        Vector2i pump,
        float defendRadius,
        int availableResource,
        IEnumerable<BlightTowerPad> pads,
        IEnumerable<Vector2i> knownHostiles)
    {
        var radius = Math.Max(12f, defendRadius);
        var hostiles = knownHostiles.ToArray();
        BlightTowerChoice? best = null;

        foreach (var pad in pads.OrderBy(p => p.EntityId))
        {
            if (pad.Occupied) continue;
            var pumpDistance = Distance(pump, pad.Position);
            if (pumpDistance < 5f || pumpDistance > radius * 1.35f) continue;

            foreach (var option in pad.Options.OrderBy(o => o.Kind))
            {
                if (option.Cost < 0 || option.Cost > availableResource) continue;
                var control = option.Kind switch
                {
                    BlightTowerKind.Chilling => 500f,
                    BlightTowerKind.Seismic => 460f,
                    BlightTowerKind.Meteor => 360f,
                    BlightTowerKind.Empowering => 300f,
                    BlightTowerKind.Summoning => 220f,
                    _ => 120f,
                };
                var idealDistance = radius * 0.65f;
                var placement = 100f - MathF.Abs(pumpDistance - idealDistance) * 3f;
                var lane = hostiles.Length == 0
                    ? 0f
                    : MathF.Max(0f, radius * 2f - hostiles.Min(h => Distance(h, pad.Position)));
                var economy = MathF.Max(0f, availableResource - option.Cost) * 0.01f;
                var score = control + placement + lane + economy;
                var choice = new BlightTowerChoice(pad.EntityId, option, score,
                    $"{option.Kind} control={control:F0} placement={placement:F0} lane={lane:F0}");
                if (best is null || choice.Score > best.Value.Score)
                    best = choice;
            }
        }

        return best;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
