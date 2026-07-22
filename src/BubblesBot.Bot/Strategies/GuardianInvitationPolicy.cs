using System.Text.RegularExpressions;
using BubblesBot.Core.Knowledge;

namespace BubblesBot.Bot.Strategies;

public readonly record struct GuardianInvitationState(
    string Name,
    bool IsTheFormed,
    int ItemQuantity,
    IReadOnlyList<string> ForbiddenLines)
{
    public bool HasForbiddenModifier => ForbiddenLines.Count > 0;
}

/// <summary>Tooltip-first invitation identity, item-quantity, and build veto policy.</summary>
public static partial class GuardianInvitationPolicy
{
    [GeneratedRegex(@"Item Quantity:\s*\+?(\d+)%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemQuantityRegex();

    public static GuardianInvitationState EvaluateTooltip(
        IEnumerable<string> tooltipLines,
        IEnumerable<string>? policyOverrides = null)
    {
        var lines = tooltipLines
            .SelectMany(x => x.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        var name = lines.FirstOrDefault(x => x.Contains("The Formed", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var quantity = -1;
        foreach (var line in lines)
        {
            var match = ItemQuantityRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
            {
                quantity = parsed;
                break;
            }
        }
        var modifierVerdict = MapModifierCatalog.EvaluateTooltip(lines, policyOverrides);
        var forbidden = modifierVerdict.Never
            .SelectMany(x => x.MatchedLines)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new GuardianInvitationState(
            name,
            name.Length > 0,
            quantity,
            forbidden);
    }

}
