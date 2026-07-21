using BubblesBot.Core.Snapshot;
using System.Text.Json;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Narrow visual oracle for a locally configured character-selection frame. The character name
/// and its derived visual mask are private runtime data loaded from LocalApplicationData; neither
/// value belongs in source control.
/// </summary>
internal static class CharacterSelectVisualOracle
{
    public const int PlayClientX = 1788;
    public const int PlayClientY = 660;

    private const int RegionX = 1390;
    private const int RegionY = 180;
    private const int RegionWidth = 500;
    private const int RegionHeight = 510;
    private const int NameX = 1410 - RegionX;
    private const int NameY = 198 - RegionY;
    private const int NameWidth = 180;
    private const int NameHeight = 20;
    private const int ExpectedMaskBytes = (NameWidth * NameHeight + 7) / 8;

    private static string PrivateProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BubblesBot", "private", "character-selection.json");

    internal sealed record Result(
        bool CaptureSucceeded,
        bool IsCharacterSelect,
        bool IdentityConfigured,
        bool NameMatches,
        bool SelectedRowMatches,
        int PlayBorderBronze,
        double NameJaccard,
        double SelectedRowLuminance,
        double NeighborRowLuminance,
        string Detail);

    public static Result Read(WindowInfo window)
    {
        if (window.Width != SystemMenuVisualOracle.SupportedWidth
            || window.Height != SystemMenuVisualOracle.SupportedHeight)
            return Failure($"unsupported window {window.Width}x{window.Height}");
        if (!ClientRegionCapture.TryCapture(
                window, RegionX, RegionY, RegionWidth, RegionHeight, out var pixels, out var error))
            return Failure(error);

        var playBorder = CountBronzeRow(pixels, globalY: 675, globalX0: 1700, globalX1: 1880);
        var selectedLuma = MeanLuminance(pixels, globalX0: 1600, globalX1: 1800, globalY0: 196, globalY1: 221);
        var neighborLuma = MeanLuminance(pixels, globalX0: 1600, globalX1: 1800, globalY0: 276, globalY1: 301);
        var currentMask = BuildNameMask(pixels);
        var configured = TryLoadPrivateProfile(out var profile, out var configurationDetail);
        var intersection = 0;
        var union = 0;
        if (configured)
        {
            for (var i = 0; i < profile.NameMask.Length; i++)
            {
                intersection += System.Numerics.BitOperations.PopCount(
                    (uint)(profile.NameMask[i] & currentMask[i]));
                union += System.Numerics.BitOperations.PopCount(
                    (uint)(profile.NameMask[i] | currentMask[i]));
            }
        }
        var jaccard = union == 0 ? 0 : (double)intersection / union;
        var frame = playBorder >= 130;
        var name = configured && jaccard >= 0.92;
        var selected = selectedLuma >= 15 && selectedLuma >= neighborLuma * 1.5;
        var detail = $"playBorder={playBorder}/180 identityConfigured={configured} nameJaccard={jaccard:F3} selectedLuma={selectedLuma:F1} neighborLuma={neighborLuma:F1}; {configurationDetail}";
        return new Result(true, frame, configured, name, selected, playBorder, jaccard, selectedLuma, neighborLuma, detail);
    }

    public static bool ConfigurationMatchesCharacter(string? characterName)
        => !string.IsNullOrWhiteSpace(characterName)
            && TryLoadPrivateProfile(out var profile, out _)
            && string.Equals(profile.CharacterName, characterName, StringComparison.Ordinal);

    private static bool TryLoadPrivateProfile(out PrivateProfile profile, out string detail)
    {
        profile = new PrivateProfile(string.Empty, []);
        if (!File.Exists(PrivateProfilePath))
        {
            detail = "private character-selection profile is missing";
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PrivateProfileDto>(File.ReadAllText(PrivateProfilePath));
            if (dto is null || string.IsNullOrWhiteSpace(dto.CharacterName)
                || string.IsNullOrWhiteSpace(dto.NameMaskBase64))
            {
                detail = "private character-selection profile is incomplete";
                return false;
            }

            var mask = Convert.FromBase64String(dto.NameMaskBase64);
            if (mask.Length != ExpectedMaskBytes)
            {
                detail = "private character-selection mask has the wrong size";
                return false;
            }

            profile = new PrivateProfile(dto.CharacterName, mask);
            detail = "private character-selection profile loaded";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            detail = $"private character-selection profile is unreadable ({ex.GetType().Name})";
            return false;
        }
    }

    private static byte[] BuildNameMask(byte[] pixels)
    {
        var mask = new byte[(NameWidth * NameHeight + 7) / 8];
        for (var i = 0; i < NameWidth * NameHeight; i++)
        {
            var x = NameX + i % NameWidth;
            var y = NameY + i / NameWidth;
            var offset = (y * RegionWidth + x) * 4;
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];
            if (r > 120 && g > 70 && b < 90)
                mask[i / 8] |= (byte)(1 << (i % 8));
        }
        return mask;
    }

    private static int CountBronzeRow(byte[] pixels, int globalY, int globalX0, int globalX1)
    {
        var y = globalY - RegionY;
        var count = 0;
        for (var globalX = globalX0; globalX < globalX1; globalX++)
        {
            var x = globalX - RegionX;
            var offset = (y * RegionWidth + x) * 4;
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];
            if (r > 80 && g > 45 && b < 80) count++;
        }
        return count;
    }

    private static double MeanLuminance(
        byte[] pixels,
        int globalX0,
        int globalX1,
        int globalY0,
        int globalY1)
    {
        long total = 0;
        var count = 0;
        for (var globalY = globalY0; globalY < globalY1; globalY++)
        for (var globalX = globalX0; globalX < globalX1; globalX++)
        {
            var x = globalX - RegionX;
            var y = globalY - RegionY;
            var offset = (y * RegionWidth + x) * 4;
            total += pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
            count += 3;
        }
        return count == 0 ? 0 : (double)total / count;
    }

    private static Result Failure(string detail)
        => new(false, false, false, false, false, 0, 0, 0, 0, detail);

    private sealed record PrivateProfile(string CharacterName, byte[] NameMask);

    private sealed class PrivateProfileDto
    {
        public string? CharacterName { get; set; }
        public string? NameMaskBase64 { get; set; }
    }
}
