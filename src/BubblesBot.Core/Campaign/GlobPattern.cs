using System.Text.RegularExpressions;

namespace BubblesBot.Core.Campaign;

/// <summary>
/// Wildcard glob matching, compatible with the AutoExile Radar plugin's area keys: <c>*</c> matches
/// any run of characters, <c>?</c> a single character; matching is case-insensitive and anchored.
/// Compiled regexes are cached per pattern since the target file reuses a small set of keys.
/// </summary>
public static class GlobPattern
{
    private static readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    public static bool Like(string input, string pattern) => ToRegex(pattern).IsMatch(input);

    public static Regex ToRegex(string pattern)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pattern, out var rx)) return rx;
            rx = new Regex(
                "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
            _cache[pattern] = rx;
            return rx;
        }
    }
}
