using BubblesBot.Core.Campaign;

namespace BubblesBot.Tests;

/// <summary>
/// Headless validation of the Phase A guidance data layer: route parsing/segmentation, glob
/// matching, the target catalog, the route↔target join/coverage, and objective selection. Runs
/// against the data embedded in BubblesBot.Core — no game required.
/// </summary>
public sealed class CampaignGuidanceTests
{
    private static CampaignData Data()
    {
        CampaignData.ResetCache();
        return CampaignData.Load();
    }

    [Fact]
    public void EmbeddedRouteAndTargetsLoadWithoutError()
    {
        var data = Data();
        Assert.Null(data.RouteError);
        Assert.Null(data.TargetsError);
        Assert.NotNull(data.Route);
        Assert.Equal(10, data.Route!.Acts.Count); // Act 1..10
        Assert.NotEmpty(data.Route.Segments);
        Assert.NotEmpty(data.Targets.AreaKeys);
    }

    [Fact]
    public void EveryKnownTokenTypeParses()
    {
        // One step containing every typed token plus a bare prose string.
        const string json = """
        [{"name":"Act 1","steps":[{"type":"fragment_step","parts":[
          "walk north",
          {"type":"enter","areaId":"1_1_2"},
          {"type":"area","areaId":"1_1_9a"},
          {"type":"kill","value":"Hillock"},
          {"type":"quest_text","value":"Glyph"},
          {"type":"quest","questId":"a1q1","rewardOffers":["a1q1","a1q1b"]},
          {"type":"waypoint_use","dstAreaId":"1_1_2","srcAreaId":"1_1_4_1"},
          {"type":"waypoint_get"},
          {"type":"waypoint"},
          {"type":"dir","dirIndex":6},
          {"type":"arena","value":"The Warden's Quarters"},
          {"type":"trial"},
          {"type":"crafting","crafting_recipes":["Vaal Skill Damage - Rank 1"]},
          {"type":"logout","areaId":"1_1_town"},
          {"type":"generic","value":"Chemist's Strongbox"},
          {"type":"ascend","version":"normal"},
          {"type":"portal_use","dstAreaId":"1_1_4_1"},
          {"type":"portal_set"}
        ],"subSteps":[]}]}]
        """;

        var route = CampaignRoute.Parse(json);
        var parts = route.Acts[0].Steps[0].Parts;
        Assert.Equal(RouteTokenType.Text, parts[0].Type);
        Assert.Equal("walk north", parts[0].Text);
        Assert.Contains(parts, t => t.Type == RouteTokenType.Dir && t.DirIndex == 6);
        Assert.Contains(parts, t => t.Type == RouteTokenType.Quest && t.RewardOffers is { Count: 2 });
        Assert.Contains(parts, t => t.Type == RouteTokenType.WaypointUse && t.DstAreaId == "1_1_2");
    }

    [Fact]
    public void UnknownTokenTypeFailsLoudly()
    {
        const string json = """
        [{"name":"Act 1","steps":[{"type":"fragment_step","parts":[{"type":"teleport","areaId":"x"}],"subSteps":[]}]}]
        """;
        Assert.Throws<CampaignRouteException>(() => CampaignRoute.Parse(json));
    }

    [Fact]
    public void SegmentationLinksAreasAndCapturesObjectives()
    {
        const string json = """
        [{"name":"Act 1","steps":[{"type":"fragment_step","parts":[
          {"type":"enter","areaId":"1_1_1"},
          {"type":"kill","value":"Hillock"},
          {"type":"enter","areaId":"1_1_town"}
        ],"subSteps":[]}]}]
        """;
        var route = CampaignRoute.Parse(json);

        var seg = route.SegmentsForArea("1_1_1");
        Assert.Single(seg);
        Assert.Equal("1_1_town", seg[0].NextAreaId);
        Assert.Contains(seg[0].Objectives, t => t.Type == RouteTokenType.Kill && t.Text == "Hillock");
    }

    [Theory]
    [InlineData("1_1_2", "*", true)]
    [InlineData("1_1_2", "1_1_*", true)]
    [InlineData("MapWorldsHax_Labyrinth_2", "*_Labyrinth_*", true)]
    [InlineData("1_1_2", "2_*", false)]
    [InlineData("Sanctum_1_1", "Sanctum*", true)]
    public void GlobMatchesAreaKeys(string area, string pattern, bool expected)
        => Assert.Equal(expected, GlobPattern.Like(area, pattern));

    [Fact]
    public void TargetsForAreaAggregatesWildcardAndExactKeys()
    {
        var data = Data();
        var pois = data.Targets.ForArea("1_1_2");
        Assert.NotEmpty(pois);
        // The "*" fallback puts a Waypoint target in every area.
        Assert.Contains(pois, t => string.Equals(t.Name, "waypoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MostRouteAreasHaveTargetCoverage()
    {
        var data = Data();
        var report = AreaCoverageReport.Build(data.Route!, data.Targets);
        Assert.NotEmpty(report);
        var covered = report.Count(c => c.HasTargets);
        // "*" applies to every area, so coverage should be essentially total; assert a strong floor.
        Assert.True(covered >= report.Count * 0.9, $"only {covered}/{report.Count} areas covered");
    }

    [Fact]
    public void SelectorProducesExitObjectiveForRoutedArea()
    {
        var data = Data();
        // Pick an early routed area that has a next area.
        var seg = data.Route!.Segments.First(s => !string.IsNullOrEmpty(s.NextAreaId));
        var plan = ObjectiveSelector.Select(seg.AreaId, data.Route, data.Targets);

        Assert.True(plan.HasRoute);
        Assert.NotEmpty(plan.Objectives);
        var exit = plan.Objectives[0];
        Assert.Equal(RouteTokenType.Enter, exit.Kind);   // exit sorts first (priority 0)
        Assert.NotEmpty(exit.Hints);
        Assert.Equal(seg.NextAreaId, plan.NextAreaId);
    }

    [Fact]
    public void SelectorFailsLoudlyWhenAreaNotInRoute()
    {
        var data = Data();
        var plan = ObjectiveSelector.Select("ZZ_not_a_real_area", data.Route, data.Targets);
        Assert.False(plan.HasRoute);
        Assert.NotNull(plan.Diagnostic);
    }
}
