using BubblesBot.Bot.Settings;

namespace BubblesBot.Bot.Modes;

public enum BlightChestCategory
{
    Currency,
    Oils,
    DivinationCards,
    Fragments,
    Essences,
    Jewels,
    Equipment,
    Talismans,
    Other,
}

/// <summary>Pure path-based reward-chest classification and user policy.</summary>
public static class BlightChestPolicy
{
    public static BlightChestCategory Classify(string? path)
    {
        if (string.IsNullOrEmpty(path)) return BlightChestCategory.Other;
        if (Contains(path, "Mushrune") || Contains(path, "Oil")) return BlightChestCategory.Oils;
        if (Contains(path, "Divination")) return BlightChestCategory.DivinationCards;
        if (Contains(path, "Currency")) return BlightChestCategory.Currency;
        if (Contains(path, "Fragment")) return BlightChestCategory.Fragments;
        if (Contains(path, "Essence")) return BlightChestCategory.Essences;
        if (Contains(path, "Trinket") || Contains(path, "Jewel")) return BlightChestCategory.Jewels;
        if (Contains(path, "Talisman")) return BlightChestCategory.Talismans;
        if (Contains(path, "Armour") || Contains(path, "Weapon")) return BlightChestCategory.Equipment;
        return BlightChestCategory.Other;
    }

    public static bool IsEnabled(string? path, BotSettings settings)
        => Classify(path) switch
        {
            BlightChestCategory.Currency => settings.BlightChestCurrency,
            BlightChestCategory.Oils => settings.BlightChestOils,
            BlightChestCategory.DivinationCards => settings.BlightChestDivinationCards,
            BlightChestCategory.Fragments => settings.BlightChestFragments,
            BlightChestCategory.Essences => settings.BlightChestEssences,
            BlightChestCategory.Jewels => settings.BlightChestJewels,
            BlightChestCategory.Equipment => settings.BlightChestEquipment,
            BlightChestCategory.Talismans => settings.BlightChestTalismans,
            _ => settings.BlightChestOther,
        };

    private static bool Contains(string path, string value)
        => path.Contains(value, StringComparison.OrdinalIgnoreCase);
}
