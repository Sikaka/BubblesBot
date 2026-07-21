using System.Globalization;

namespace BubblesBot.Research.Probing;

/// <summary>
/// The three-way truth table, centralized so each probe's Validate is one call. Compares what we
/// read (OURS) against the gitignored baseline (BASE, the authority) and the optional oracle
/// (ORACLE, a cross-check). One key string names both the baseline fact and the oracle value.
///
/// <list type="bullet">
///   <item>OURS == BASE, no oracle ............ PASS (independent)</item>
///   <item>OURS == BASE == ORACLE ............. PASS (corroborated)</item>
///   <item>OURS == BASE, ORACLE != BASE ....... CONFLICT (baseline stale; re-capture)</item>
///   <item>OURS != BASE ........................ FAIL (offset drifted)</item>
///   <item>no BASE, OURS == ORACLE ............. PASS (oracle-only; capture a baseline)</item>
///   <item>no BASE, OURS != ORACLE ............. FAIL</item>
///   <item>no BASE, no ORACLE .................. SKIP (nothing to check against)</item>
/// </list>
/// </summary>
public static class Check
{
    public static ProbeResult Int(ProbeContext ctx, string key, int ours, string where)
    {
        var hasBase = ctx.Facts.TryGetInt(key, out var bse);
        var hasOracle = TryOracleInt(ctx, key, out var orc);
        return Decide(where, key, ours.ToString(CultureInfo.InvariantCulture),
            hasBase, bse.ToString(CultureInfo.InvariantCulture), ours == bse,
            hasOracle, orc.ToString(CultureInfo.InvariantCulture), ours == orc,
            baseNeqOracle: hasBase && hasOracle && bse != orc);
    }

    public static ProbeResult Long(ProbeContext ctx, string key, long ours, string where)
    {
        var hasBase = ctx.Facts.TryGetLong(key, out var bse);
        var hasOracle = TryOracleLong(ctx, key, out var orc);
        return Decide(where, key, ours.ToString(CultureInfo.InvariantCulture),
            hasBase, bse.ToString(CultureInfo.InvariantCulture), ours == bse,
            hasOracle, orc.ToString(CultureInfo.InvariantCulture), ours == orc,
            baseNeqOracle: hasBase && hasOracle && bse != orc);
    }

    public static ProbeResult Str(ProbeContext ctx, string key, string ours, string where)
    {
        var hasBase = ctx.Facts.TryGetStr(key, out var bse);
        var hasOracle = ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out _);
        var orc = "";
        if (hasOracle) ctx.Oracle.TryGetValue(key, out orc);
        return Decide(where, key, Quote(ours),
            hasBase, Quote(bse), string.Equals(ours, bse, StringComparison.Ordinal),
            hasOracle, Quote(orc), string.Equals(ours, orc, StringComparison.Ordinal),
            baseNeqOracle: hasBase && hasOracle && !string.Equals(bse, orc, StringComparison.Ordinal));
    }

    public static ProbeResult Float(ProbeContext ctx, string key, float ours, string where, float epsilon = 0.5f)
    {
        var hasBase = ctx.Facts.TryGetFloat(key, out var bse);
        var hasOracle = ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out var os)
            && float.TryParse(os, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed);
        var orc = 0f;
        if (hasOracle) { ctx.Oracle.TryGetValue(key, out var s); float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out orc); }
        return Decide(where, key, F(ours),
            hasBase, F(bse), Math.Abs(ours - bse) <= epsilon,
            hasOracle, F(orc), Math.Abs(ours - orc) <= epsilon,
            baseNeqOracle: hasBase && hasOracle && Math.Abs(bse - orc) > epsilon);
    }

    /// <summary>
    /// Pointer-field check. With an oracle, compare OURS to the oracle-supplied heap ADDRESS
    /// (exact). Without one, fall back to structural soundness (non-null + optional validator);
    /// pass <paramref name="requireNonNull"/>=false for fields that are legitimately null when
    /// absent (e.g. a closed UI panel).
    /// </summary>
    public static ProbeResult Address(ProbeContext ctx, string oracleKey, nint ours, string where,
        bool requireNonNull = true, Func<nint, bool>? sound = null)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetAddress(oracleKey, out var truth))
            return ours == truth
                ? ProbeResult.Pass($"{where} = 0x{(long)ours:X} (oracle ok)")
                : ProbeResult.Fail($"{where}: ours = 0x{(long)ours:X} != oracle = 0x{(long)truth:X}");

        if (ours == 0)
            return requireNonNull
                ? ProbeResult.Fail($"{where}: null pointer")
                : ProbeResult.Pass($"{where}: null (absent/closed)");
        if (sound is not null && !sound(ours))
            return ProbeResult.Fail($"{where}: 0x{(long)ours:X} failed soundness check");
        return ProbeResult.Pass($"{where} = 0x{(long)ours:X} (sound; no oracle)");
    }

    /// <summary>
    /// Volatile scalar (grid pos, monsters-remaining, ...) that a stale baseline can't authoritatively
    /// pin. With an oracle, exact-compare; without one, accept any value in a plausible range.
    /// </summary>
    public static ProbeResult Live(ProbeContext ctx, string oracleKey, long ours, string where, long min, long max)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(oracleKey, out var s)
            && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var truth))
            return ours == truth
                ? ProbeResult.Pass($"{where} = {ours} (oracle ok)")
                : ProbeResult.Fail($"{where}: ours = {ours} != oracle = {truth}");
        return ours >= min && ours <= max
            ? ProbeResult.Pass($"{where} = {ours} (in range; no oracle)")
            : ProbeResult.Fail($"{where} = {ours} out of plausible range [{min}, {max}]");
    }

    private static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryOracleInt(ProbeContext ctx, string key, out int value)
    {
        value = 0;
        return ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out var s)
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryOracleLong(ProbeContext ctx, string key, out long value)
    {
        value = 0;
        return ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out var s)
            && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static ProbeResult Decide(
        string where, string key, string oursStr,
        bool hasBase, string baseStr, bool oursEqBase,
        bool hasOracle, string oracleStr, bool oursEqOracle,
        bool baseNeqOracle)
    {
        if (hasBase)
        {
            if (oursEqBase)
            {
                return baseNeqOracle
                    ? ProbeResult.Conflict($"{where} = {oursStr} (== baseline) but oracle = {oracleStr} -- baseline stale, re-capture")
                    : ProbeResult.Pass($"{where} = {oursStr}{(hasOracle ? " (oracle agrees)" : "")}");
            }
            return ProbeResult.Fail($"{where}: ours = {oursStr} != baseline = {baseStr}");
        }

        if (hasOracle)
        {
            return oursEqOracle
                ? ProbeResult.Pass($"{where} = {oursStr} (oracle-only; run --baseline capture)")
                : ProbeResult.Fail($"{where}: ours = {oursStr} != oracle = {oracleStr} (no baseline)");
        }

        return ProbeResult.Skip($"{where}: no baseline fact '{key}' and no oracle");
    }

    private static string Quote(string s) => $"\"{s}\"";
}
