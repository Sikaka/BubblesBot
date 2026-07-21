using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Unified "which UI windows are open right now" sweep over the <c>IngameUiElements</c>
/// panel table. This is the bot's situational awareness for menus: confirming a panel we
/// meant to open actually opened, noticing panels we did NOT intend to open (accidental
/// clicks), and verifying a close actually closed.
///
/// <para><b>Two signals per panel</b>, because PoE is inconsistent about lifecycle: some
/// panels null their IngameUi pointer when closed, others keep the element allocated and
/// just clear the visibility bit. <see cref="PanelState.Present"/> = pointer non-null;
/// <see cref="PanelState.Visible"/> = full parent-chain visibility walk
/// (<see cref="ElementReader.IsVisibleDeep"/>). <see cref="PanelState.IsOpen"/> requires
/// both — the conservative reading validated per-panel via <c>--watch-ui-panels</c>.</para>
///
/// <para>Cost: one pointer read per closed panel (null short-circuits), plus a short
/// flags/parent walk per present panel. Cheap enough to sweep at world rate; consumers use
/// the <see cref="GameSnapshot.OpenPanels"/> accessor.</para>
/// </summary>
public sealed class OpenPanelsView
{
    public readonly record struct PanelState(string Name, bool Present, bool Visible)
    {
        /// <summary>Open = allocated AND actually visible to the player.</summary>
        public bool IsOpen => Present && Visible;
    }

    /// <summary>
    /// Every window-like panel in the IngameUi table. Infrastructure elements (skill bar,
    /// minimap, chat, label roots, tooltips) are deliberately excluded — this catalog is
    /// about panels that occupy screen space and change what a click means.
    /// LeftPanel/RightPanel are PoE's own dock aggregates (whatever is docked on each side).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, int Offset)> Catalog =
    [
        ("LeftPanel",                    KnownOffsets.IngameUiElements.OpenLeftPanel),
        ("RightPanel",                   KnownOffsets.IngameUiElements.OpenRightPanel),
        ("InventoryPanel",               KnownOffsets.IngameUiElements.InventoryPanel),
        ("StashElement",                 KnownOffsets.IngameUiElements.StashElement),
        ("GuildStashElement",            KnownOffsets.IngameUiElements.GuildStashElement),
        ("OfflineMerchantPanel",         KnownOffsets.IngameUiElements.OfflineMerchantPanel),
        ("TreePanel",                    KnownOffsets.IngameUiElements.TreePanel),
        ("AtlasPanel",                   KnownOffsets.IngameUiElements.AtlasPanel),
        ("WorldMap",                     KnownOffsets.IngameUiElements.WorldMap),
        ("NpcDialog",                    KnownOffsets.IngameUiElements.NpcDialog),
        ("PurchaseWindow",               KnownOffsets.IngameUiElements.PurchaseWindow),
        ("SellWindow",                   KnownOffsets.IngameUiElements.SellWindow),
        ("TradeWindow",                  KnownOffsets.IngameUiElements.TradeWindow),
        ("MapReceptacleWindow",          KnownOffsets.IngameUiElements.MapReceptacleWindow),
        ("LabyrinthDivineFontPanel",     KnownOffsets.IngameUiElements.LabyrinthDivineFontPanel),
        ("IncursionWindow",              KnownOffsets.IngameUiElements.IncursionWindow),
        ("DelveWindow",                  KnownOffsets.IngameUiElements.DelveWindow),
        ("BetrayalWindow",               KnownOffsets.IngameUiElements.BetrayalWindow),
        ("ZanaMissionChoice",            KnownOffsets.IngameUiElements.ZanaMissionChoice),
        ("CraftBenchWindow",             KnownOffsets.IngameUiElements.CraftBenchWindow),
        ("UnveilWindow",                 KnownOffsets.IngameUiElements.UnveilWindow),
        ("HeistWindow",                  KnownOffsets.IngameUiElements.HeistWindow),
        ("BlueprintWindow",              KnownOffsets.IngameUiElements.BlueprintWindow),
        ("HeistLockerElement",           KnownOffsets.IngameUiElements.HeistLockerElement),
        ("RitualWindow",                 KnownOffsets.IngameUiElements.RitualWindow),
        ("UltimatumPanel",               KnownOffsets.IngameUiElements.UltimatumPanel),
        ("ExpeditionWindow",             KnownOffsets.IngameUiElements.ExpeditionWindow),
        ("ExpeditionLockerElement",      KnownOffsets.IngameUiElements.ExpeditionLockerElement),
        ("SanctumFloorWindow",           KnownOffsets.IngameUiElements.SanctumFloorWindow),
        ("SanctumRewardWindow",          KnownOffsets.IngameUiElements.SanctumRewardWindow),
        ("NecropolisMonsterPanel",       KnownOffsets.IngameUiElements.NecropolisMonsterPanel),
        ("VillageRecruitmentPanel",      KnownOffsets.IngameUiElements.VillageRecruitmentPanel),
        ("VillageRewardWindow",          KnownOffsets.IngameUiElements.VillageRewardWindow),
        ("VillageShipmentScreen",        KnownOffsets.IngameUiElements.VillageShipmentScreen),
        ("VillageWorkerManagementPanel", KnownOffsets.IngameUiElements.VillageWorkerManagementPanel),
        ("VillageScreen",                KnownOffsets.IngameUiElements.VillageScreen),
        ("MercenaryEncounterWindow",     KnownOffsets.IngameUiElements.MercenaryEncounterWindow),
        ("GenesisTreeWindow",            KnownOffsets.IngameUiElements.GenesisTreeWindow),
        ("CurrencyExchangePanel",        KnownOffsets.IngameUiElements.CurrencyExchangePanel),
        ("ItemRightClickPriceMenu",      KnownOffsets.IngameUiElements.ItemRightClickPriceMenu),
        ("CurrencyShiftClickMenu",       KnownOffsets.IngameUiElements.CurrencyShiftClickMenu),
        ("AsyncItemRightClickPriceMenu", KnownOffsets.IngameUiElements.AsyncItemRightClickPriceMenu),
        ("PopUpWindow",                  KnownOffsets.IngameUiElements.PopUpWindow),
        ("InstanceManagerPanel",         KnownOffsets.IngameUiElements.InstanceManagerPanel),
        ("ResurrectPanel",               KnownOffsets.IngameUiElements.ResurrectPanel),
        ("GemLvlUpPanel",                KnownOffsets.IngameUiElements.GemLvlUpPanel),
        ("BlightEncounterUi",            KnownOffsets.IngameUiElements.BlightEncounterUi),
        ("InvitesPanel",                 KnownOffsets.IngameUiElements.InvitesPanel),
    ];

    /// <summary>
    /// Panels that, when open, cover the game and swallow/redirect world clicks — so the bot
    /// must not walk, cast, or click the world while one is up (an accidental click landed on a
    /// vendor/NPC/dialog, a trade opened, the player died). This is the modal set the
    /// <c>UiContext</c> gate and <c>PanelGuard</c> act on. Inventory/Stash/Atlas are deliberately
    /// NOT here — those are opened intentionally by farming flows and are handled per-mechanic.
    ///
    /// <para><b>Excluded — unreliable "open" signal (found live 2026-07-14):</b> GemLvlUpPanel
    /// and InvitesPanel report <c>IsOpen == true</c> from their persistent visible bit even when
    /// nothing is shown (POEMCP can't corroborate them). Including them here wedged
    /// <see cref="IsWorldBlocked"/> permanently true. They need a more specific open-signal
    /// (child-content inspection) before they can gate anything — tracked for a later RE pass.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> BlockingPanels = new HashSet<string>(StringComparer.Ordinal)
    {
        "NpcDialog", "PurchaseWindow", "SellWindow", "TradeWindow",
        "ResurrectPanel", "PopUpWindow",
    };

    private readonly IReadOnlyList<PanelState> _states;
    private readonly List<string> _open;

    private OpenPanelsView(IReadOnlyList<PanelState> states)
    {
        _states = states;
        _open = new List<string>();
        foreach (var s in states)
            if (s.IsOpen) _open.Add(s.Name);
    }

    /// <summary>All catalog panels with both raw signals — diagnostics and the watcher use this.</summary>
    public IReadOnlyList<PanelState> States => _states;

    /// <summary>Names of panels that are open (present + visible) right now.</summary>
    public IReadOnlyList<string> Open => _open;

    public bool IsOpen(string name)
    {
        foreach (var s in _states)
            if (s.IsOpen && string.Equals(s.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Panels open beyond an expected set — the accidental-click signal.</summary>
    public IReadOnlyList<string> OpenExcept(IReadOnlySet<string> expected)
    {
        var extra = new List<string>();
        foreach (var name in _open)
            if (!expected.Contains(name)) extra.Add(name);
        return extra;
    }

    /// <summary>Open panels that are in the modal <see cref="BlockingPanels"/> set.</summary>
    public IReadOnlyList<string> BlockingOpen()
    {
        var b = new List<string>();
        foreach (var name in _open)
            if (BlockingPanels.Contains(name)) b.Add(name);
        return b;
    }

    /// <summary>True when a modal panel covers the game and world actions should be suppressed.</summary>
    public bool IsWorldBlocked()
    {
        foreach (var name in _open)
            if (BlockingPanels.Contains(name)) return true;
        return false;
    }

    public static OpenPanelsView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        var states = new PanelState[Catalog.Count];

        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0)
        {
            for (var i = 0; i < Catalog.Count; i++)
                states[i] = new PanelState(Catalog[i].Name, false, false);
            return new OpenPanelsView(states);
        }

        for (var i = 0; i < Catalog.Count; i++)
        {
            var (name, offset) = Catalog[i];
            var present = reader.TryReadStruct<nint>(ingameUi + offset, out var panel) && panel != 0;
            var visible = present && ElementReader.IsVisibleDeep(reader, panel);
            states[i] = new PanelState(name, present, visible);
        }
        return new OpenPanelsView(states);
    }
}
