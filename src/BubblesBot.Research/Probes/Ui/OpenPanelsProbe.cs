using System.Text.Json;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Ui;

/// <summary>
/// The unified open-window sweep (<see cref="OpenPanelsView"/>): every catalog panel must
/// read cleanly, and — when POEMCP is reachable — our open/closed verdicts must agree with
/// ExileCore's for the seven panels its <c>/state/ui</c> endpoint reports. The REST GET
/// works without game focus (no game-thread eval), so this cross-check is nearly free.
///
/// <para>Interactive coverage (flip detection while a human opens/closes menus) lives in
/// <c>--watch-ui-panels</c>, not here — a probe run only sees the current instant.</para>
/// </summary>
public sealed class OpenPanelsProbe : IProbe
{
    public string Name => "ui.openpanels";
    public string Group => "ui";
    public string Description => "OpenPanelsView sweep reads all panels; agrees with POEMCP /state/ui where mapped.";
    public IReadOnlyList<string> RequiredFacts => [];

    // OpenPanelsView catalog name -> POEMCP /state/ui property.
    private static readonly (string Panel, string UiKey)[] PoemcpMap =
    [
        ("InventoryPanel", "inventoryVisible"),
        ("StashElement",   "stashVisible"),
        ("NpcDialog",      "npcDialogVisible"),
        ("AtlasPanel",     "atlasPanelVisible"),
        ("SellWindow",     "sellWindowVisible"),
        ("PurchaseWindow", "purchaseWindowVisible"),
        ("ResurrectPanel", "resurrectPanelVisible"),
    ];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var view = OpenPanelsView.FromIngameUi(ctx.Reader, ctx.Chain.IngameState);
        if (view.States.Count != OpenPanelsView.Catalog.Count)
            return ProbeResult.Fail($"sweep returned {view.States.Count} states for {OpenPanelsView.Catalog.Count} catalog entries");

        var presentCount = 0;
        foreach (var s in view.States) if (s.Present) presentCount++;
        // An in-game UI always keeps a healthy share of panel elements allocated. Zero would
        // mean the IngameUi pointer or the table offsets drifted.
        if (presentCount == 0)
            return ProbeResult.Fail("no panel pointers present at all — IngameUiElements offsets likely drifted");

        var truth = TryFetchPoemcpUi();
        if (truth is null)
            return ProbeResult.Pass($"{presentCount}/{view.States.Count} panels present, "
                + $"open: [{string.Join(", ", view.Open)}] (POEMCP unreachable — structural only)");

        var results = new List<ProbeResult>();
        foreach (var (panel, uiKey) in PoemcpMap)
        {
            if (!truth.Value.TryGetProperty(uiKey, out var el)) continue;
            var oracle = el.GetBoolean();
            var ours = view.IsOpen(panel);
            results.Add(ours == oracle
                ? ProbeResult.Pass($"{panel} = {ours} (POEMCP agrees)")
                : ProbeResult.Fail($"{panel}: ours = {ours} != POEMCP {uiKey} = {oracle}"));
        }
        results.Add(ProbeResult.Pass($"{presentCount}/{view.States.Count} panels present, open: [{string.Join(", ", view.Open)}]"));
        return ProbeResult.Combine(results.ToArray());
    }

    public ProbeResult Discover(ProbeContext ctx)
        => ProbeResult.Skip("panel offsets drift individually — re-derive from ExileCore's GameOffsets dump, "
            + "then use --watch-ui-panels to re-validate lifecycles");

    private static JsonElement? TryFetchPoemcpUi()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            var json = http.GetStringAsync("http://localhost:5999/state/ui").GetAwaiter().GetResult();
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
