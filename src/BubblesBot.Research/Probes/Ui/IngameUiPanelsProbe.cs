using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Ui;

/// <summary>
/// The IngameUi panel pointers the bot navigates. Each is validated against the oracle address
/// when present, else as a sound UiElement (or legitimately null when the panel is closed).
/// </summary>
public sealed class IngameUiPanelsProbe : IProbe
{
    private static readonly (int Off, string OracleKey, string Name)[] Panels =
    {
        (KnownOffsets.IngameUiElements.GameUI,         "ui.gameui",         "GameUI"),
        (KnownOffsets.IngameUiElements.SkillBar,       "ui.skillbar",       "SkillBar"),
        (KnownOffsets.IngameUiElements.HiddenSkillBar, "ui.hiddenskillbar", "HiddenSkillBar"),
        (KnownOffsets.IngameUiElements.QuestTracker,   "ui.questtracker",   "QuestTracker"),
        (KnownOffsets.IngameUiElements.OpenLeftPanel,  "ui.openleftpanel",  "OpenLeftPanel"),
        (KnownOffsets.IngameUiElements.OpenRightPanel, "ui.openrightpanel", "OpenRightPanel"),
        (KnownOffsets.IngameUiElements.InventoryPanel, "ui.inventorypanel", "InventoryPanel"),
        (KnownOffsets.IngameUiElements.StashElement,   "ui.stashelement",   "StashElement"),
        (KnownOffsets.IngameUiElements.MapSideUI,      "ui.mapsideui",      "MapSideUI"),
    };

    public string Name => "ui.panels";
    public string Group => "ui";
    public string Description => "IngameUi panel pointers resolve to sound UiElements.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var ui = ctx.Chain.IngameUi;
        if (ui == 0) return ProbeResult.Fail("IngameUi pointer null");
        return ProbeResult.Combine(Panels.Select(p =>
        {
            var ptr = ctx.Reader.TryReadStruct<nint>(ui + p.Off, out var v) ? v : 0;
            return Check.Address(ctx, p.OracleKey, ptr, $"IngameUi.{p.Name}@+0x{p.Off:X}",
                requireNonNull: false, a => Reads.IsElement(ctx.Reader, a));
        }).ToArray());
    }

    public ProbeResult Discover(ProbeContext ctx)
        => Discovery.Pointer(ctx, ctx.Chain.IngameUi, "ui.inventorypanel", 0x800, "IngameUi.InventoryPanel");
}
