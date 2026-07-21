namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Scans a UI tree from a root element, returning <see cref="PatternMatch"/> candidates
/// ranked by confidence. Uses BFS bounded by depth so a wrong starting point can't run
/// away into millions of elements.
///
/// <para><b>Confidence model:</b> a perfect-match pattern (every constraint satisfied,
/// including all sub-children) scores 1.0. Partial matches dock based on which constraints
/// failed — child count off by 1 hurts less than a missing distinctive sub-child. The
/// scoring is heuristic and tunable; what matters is RANKING, not the absolute number.</para>
///
/// <para>The discovery tool prints the top N candidates so a human can confirm. Once
/// confirmed, the path goes into <see cref="UiIndexPaths"/> and runtime navigation uses
/// <see cref="UiTreeNavigator"/> directly — fast, deterministic, no shape matching.</para>
/// </summary>
public static class UiPatternMatcher
{
    /// <summary>Default cap on tree-walk depth from the starting root.</summary>
    public const int DefaultMaxDepth = 8;

    /// <summary>Default cap on total elements visited per scan (defensive against runaway trees).</summary>
    public const int DefaultMaxVisits = 50_000;

    /// <summary>
    /// Find all candidates matching <paramref name="pattern"/>, ordered by confidence
    /// descending. Empty list = no element in the tree even partially matched.
    /// </summary>
    public static List<PatternMatch> Find(MemoryReader reader, nint rootElement, UiPattern pattern,
        int maxDepth = DefaultMaxDepth, int maxVisits = DefaultMaxVisits, float minConfidence = 0.4f)
    {
        var results = new List<PatternMatch>();
        if (rootElement == 0) return results;

        // BFS, tracking the path as we go. Each entry is (address, depth, indexPathSoFar).
        var queue = new Queue<(nint addr, int depth, int[] path)>();
        queue.Enqueue((rootElement, 0, Array.Empty<int>()));
        var visited = 0;

        while (queue.Count > 0 && visited < maxVisits)
        {
            var (addr, depth, path) = queue.Dequeue();
            visited++;

            // Score this element against the pattern.
            var (conf, notes) = ScoreMatch(reader, addr, pattern);
            if (conf >= minConfidence)
                results.Add(new PatternMatch(new UiIndexPath(path), conf, notes));

            // Continue BFS into children up to depth limit.
            if (depth >= maxDepth) continue;
            var count = UiTreeNavigator.ChildCount(reader, addr);
            if (count == 0 || count > 256) continue;
            for (var i = 0; i < count; i++)
            {
                var child = UiTreeNavigator.ChildAt(reader, addr, i);
                if (child == 0) continue;
                var nextPath = new int[path.Length + 1];
                Array.Copy(path, nextPath, path.Length);
                nextPath[path.Length] = i;
                queue.Enqueue((child, depth + 1, nextPath));
            }
        }

        results.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        return results;
    }

    /// <summary>
    /// Convenience: return the single best match or null. Equivalent to <c>Find(...).FirstOrDefault()</c>
    /// with a sane null-handling result type.
    /// </summary>
    public static PatternMatch? FindBest(MemoryReader reader, nint rootElement, UiPattern pattern,
        int maxDepth = DefaultMaxDepth, int maxVisits = DefaultMaxVisits)
    {
        var all = Find(reader, rootElement, pattern, maxDepth, maxVisits);
        return all.Count == 0 ? null : all[0];
    }

    // ── Scoring ──────────────────────────────────────────────────────────

    private static (float confidence, string notes) ScoreMatch(MemoryReader reader, nint addr, UiPattern pattern)
    {
        // Get child count first — most patterns key on it.
        var childCount = UiTreeNavigator.ChildCount(reader, addr);

        // Top-level child count constraint.
        var (countOk, countWeight) = MatchCount(childCount, pattern.ChildCountExact, pattern.MinChildCount, pattern.MaxChildCount);
        if (!countOk) return (0f, $"child count {childCount} out of range");

        // No further constraints → just count match counts.
        if (pattern.Children.Length == 0) return (countWeight, $"count={childCount} (no child specs)");

        // Score each child spec.
        var totalWeight = countWeight;
        var maxWeight = countWeight;
        var hits = 0;
        var miss = "";
        foreach (var spec in pattern.Children)
        {
            maxWeight += 1f;
            if (spec.Index >= childCount)
            {
                miss = $"child[{spec.Index}] missing";
                continue;
            }
            var childAddr = UiTreeNavigator.ChildAt(reader, addr, spec.Index);
            var (childOk, childWeight) = ScoreChildSpec(reader, childAddr, spec);
            totalWeight += childWeight;
            if (childOk) hits++;
            else if (string.IsNullOrEmpty(miss)) miss = $"child[{spec.Index}] partial ({childWeight:F2})";
        }

        var confidence = maxWeight > 0 ? totalWeight / maxWeight : 0f;
        var notes = hits == pattern.Children.Length
            ? $"all {hits} child specs matched"
            : $"{hits}/{pattern.Children.Length} child specs matched ({miss})";
        return (confidence, notes);
    }

    /// <summary>
    /// Score a single child against its spec. Returns <c>(strictMatch, weight)</c> where
    /// strictMatch = "every sub-constraint passed exactly," weight is a [0..1] partial score.
    /// </summary>
    private static (bool ok, float weight) ScoreChildSpec(MemoryReader reader, nint childAddr, UiChildSpec spec)
    {
        if (childAddr == 0) return (false, 0f);

        // Child count check on this child element.
        var subCount = UiTreeNavigator.ChildCount(reader, childAddr);
        var (countOk, countWeight) = MatchCount(subCount, spec.ChildCountExact, spec.MinChildCount, spec.MaxChildCount);
        if (!countOk) return (false, countWeight * 0.5f); // penalize but don't zero

        if (spec.Children is null || spec.Children.Length == 0)
            return (true, countWeight);

        // Score nested specs.
        var nestedTotal = countWeight;
        var nestedMax = countWeight;
        var nestedAllOk = true;
        foreach (var sub in spec.Children)
        {
            nestedMax += 1f;
            if (sub.Index >= subCount) { nestedAllOk = false; continue; }
            var subChild = UiTreeNavigator.ChildAt(reader, childAddr, sub.Index);
            var (subOk, subWeight) = ScoreChildSpec(reader, subChild, sub);
            nestedTotal += subWeight;
            if (!subOk) nestedAllOk = false;
        }
        return (nestedAllOk, nestedMax > 0 ? nestedTotal / nestedMax : 0f);
    }

    private static (bool ok, float weight) MatchCount(int actual, int? exact, int? min, int? max)
    {
        if (exact is { } e)
        {
            if (actual == e) return (true, 1f);
            // Off-by-one is suspicious-but-not-disqualifying — partial credit.
            return Math.Abs(actual - e) <= 1 ? (false, 0.5f) : (false, 0f);
        }
        if (min is { } mn && actual < mn) return (false, 0f);
        if (max is { } mx && actual > mx) return (false, 0f);
        return (true, 1f);
    }
}
