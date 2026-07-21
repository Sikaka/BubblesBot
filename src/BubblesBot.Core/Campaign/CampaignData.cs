using System.Reflection;

namespace BubblesBot.Core.Campaign;

/// <summary>
/// Loads the campaign route + target catalog once and caches them. Data ships embedded in the Core
/// assembly (route from exile-leveling, targets adapted from the Radar plugin) so the bot needs no
/// filesystem access; an optional directory override lets dev/probe point at newer files.
/// </summary>
public sealed class CampaignData
{
    public CampaignRoute? Route { get; }
    public CampaignTargets Targets { get; }
    public string? RouteError { get; }
    public string? TargetsError { get; }

    private CampaignData(CampaignRoute? route, CampaignTargets targets, string? routeErr, string? targetsErr)
    {
        Route = route;
        Targets = targets;
        RouteError = routeErr;
        TargetsError = targetsErr;
    }

    private static CampaignData? _cached;
    private static readonly object _lock = new();

    /// <summary>Load (or return cached) campaign data. When <paramref name="overrideDir"/> is set and
    /// contains <c>campaign-route.json</c> / <c>campaign-targets.json</c>, those win over embedded.</summary>
    public static CampaignData Load(string? overrideDir = null)
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            _cached = LoadUncached(overrideDir);
            return _cached;
        }
    }

    public static void ResetCache()
    {
        lock (_lock) _cached = null;
    }

    private static CampaignData LoadUncached(string? overrideDir)
    {
        var routeJson = ReadText(overrideDir, "campaign-route.json");
        var targetsJson = ReadText(overrideDir, "campaign-targets.json");

        CampaignRoute? route = null;
        string? routeErr = null;
        try { route = routeJson is null ? null : CampaignRoute.Parse(routeJson); }
        catch (Exception ex) { routeErr = ex.Message; }
        if (routeJson is null) routeErr = "campaign-route.json not found";

        CampaignTargets targets = CampaignTargets.Empty;
        string? targetsErr = null;
        try { if (targetsJson is not null) targets = CampaignTargets.Parse(targetsJson); }
        catch (Exception ex) { targetsErr = ex.Message; }
        if (targetsJson is null) targetsErr = "campaign-targets.json not found";

        return new CampaignData(route, targets, routeErr, targetsErr);
    }

    private static string? ReadText(string? overrideDir, string fileName)
    {
        if (!string.IsNullOrEmpty(overrideDir))
        {
            var path = System.IO.Path.Combine(overrideDir, fileName);
            if (System.IO.File.Exists(path)) return System.IO.File.ReadAllText(path);
        }

        var asm = typeof(CampaignData).Assembly;
        var resName = $"{asm.GetName().Name}.Resources.{fileName}";
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream is null) return null;
        using var sr = new System.IO.StreamReader(stream);
        return sr.ReadToEnd();
    }
}
