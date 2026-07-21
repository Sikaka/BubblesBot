using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Reads the encounter's tower currency from the Blight HUD. The direct panel pointer and
/// child path mirror the live AutoExile contract:
/// BlightEncounterUi[0][3][2][0][1]. Absence is represented as unknown rather than zero so
/// damage-tower budgeting can fail closed without blocking defensive control towers.
/// </summary>
public readonly record struct BlightCurrencyView(int? Currency, string RawText, bool IsVisible)
{
    public static BlightCurrencyView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(
                ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0)
            return default;
        reader.TryReadStruct<nint>(
            ingameUi + KnownOffsets.IngameUiElements.BlightEncounterUi, out var panel);

        // Builds have exposed this pointer at slightly different depths. Try the validated
        // direct shape, the two inner shapes, then AutoExile's top-level child[11] route.
        var candidates = new[]
        {
            Resolve(reader, panel, 0, 3, 2, 0, 1),
            Resolve(reader, panel, 3, 2, 0, 1),
            Resolve(reader, panel, 2, 0, 1),
            Resolve(reader, Resolve(reader, ingameUi, 11), 0, 3, 2, 0, 1),
        };
        foreach (var textElement in candidates.Distinct())
        {
            if (textElement == 0 || !ElementReader.IsVisibleDeep(reader, textElement)) continue;
            var text = ReadText(reader, textElement);
            if (TryParse(text, out var currency))
                return new BlightCurrencyView(currency, text, true);
        }

        // AutoExile fallback: the top-level child index has shifted between game builds, but
        // the Blight HUD's internal shape and its "Pump Durability" landmark are stable.
        // Search bounded top-level children, positively identify the HUD by that label, then
        // read its sibling currency field. This avoids guessing that an unrelated integer is
        // tower currency.
        var ui = ElementReader.TryReadSnapshot(reader, ingameUi, 256);
        if (ui is not null)
        {
            for (var i = 0; i < Math.Min(40, ui.Children.Count); i++)
            {
                var top = ui.Children[i];
                var inner = Resolve(reader, top, 0);
                var hud = Resolve(reader, inner, 3);
                if (hud == 0 || !ElementReader.IsVisibleDeep(reader, hud)) continue;
                var durability = Resolve(reader, hud, 1, 0, 0);
                var durabilityText = ReadText(reader, durability);
                if (!durabilityText.Contains("Pump", StringComparison.OrdinalIgnoreCase)) continue;
                var currencyElement = Resolve(reader, hud, 2, 0, 1);
                var currencyText = ReadText(reader, currencyElement);
                if (TryParse(currencyText, out var currency))
                    return new BlightCurrencyView(currency, currencyText, true);
            }
        }
        return default;
    }

    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return int.TryParse(text.Replace(",", "", StringComparison.Ordinal).Trim(), out value)
            && value >= 0;
    }

    private static nint Resolve(MemoryReader reader, nint root, params int[] path)
    {
        var current = root;
        foreach (var index in path)
            if (!ElementReader.TryGetChild(reader, current, index, out current)) return 0;
        return current;
    }

    private static string ReadText(MemoryReader reader, nint element)
    {
        if (element == 0) return string.Empty;
        var text = NativeString.Read(reader, element + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, element + KnownOffsets.Element.Text)
            : text;
    }
}
