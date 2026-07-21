using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Narrow 1920x1080 visual classifier for PoE 1's six-button system menu. It samples four
/// repeated bronze button-border bands from a freshly captured client region. This is research
/// groundwork, not a general screenshot/OCR framework.
/// </summary>
internal static class SystemMenuVisualOracle
{
    public const int SupportedWidth = 1920;
    public const int SupportedHeight = 1080;

    private const int RegionX = 790;
    private const int RegionY = 267;
    private const int RegionWidth = 340;
    private const int RegionHeight = 286;
    private static readonly int[] BorderRows = [374, 415, 456, 497];

    internal sealed record Result(
        bool CaptureSucceeded,
        bool IsOpen,
        int MatchingBands,
        IReadOnlyList<int> BronzeCounts,
        string Detail);

    public static Result Read(WindowInfo window)
    {
        if (window.Width != SupportedWidth || window.Height != SupportedHeight)
            return new Result(false, false, 0, [],
                $"unsupported window {window.Width}x{window.Height}");
        if (!ClientRegionCapture.TryCapture(
                window, RegionX, RegionY, RegionWidth, RegionHeight, out var pixels, out var error))
            return new Result(false, false, 0, [], error);

        var counts = new int[BorderRows.Length];
        for (var rowIndex = 0; rowIndex < BorderRows.Length; rowIndex++)
        {
            var localY = BorderRows[rowIndex] - RegionY;
            var count = 0;
            for (var localX = 0; localX < RegionWidth; localX += 2)
            {
                var offset = (localY * RegionWidth + localX) * 4;
                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];
                if (r > 80 && g > 45 && b < 80) count++;
            }
            counts[rowIndex] = count;
        }

        // The live open fixture produced 143/170 bronze samples on each repeated band; the
        // same rows in the closed world fixture peaked at 17. A threshold of 90 leaves wide
        // separation while tolerating hover animation and scene variation. Require 3/4 bands.
        var matching = counts.Count(x => x >= 90);
        return new Result(true, matching >= 3, matching, counts,
            $"matchingBands={matching}/4 bronze=[{string.Join(',', counts)}]");
    }

}
