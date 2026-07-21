namespace BubblesBot.Research.Probing.Oracle;

/// <summary>
/// Maps probe fact/struct keys to the ExileAPI (POEMCP) expressions that yield them. This is the
/// ONLY place that knows ExileAPI's API shape; probes refer to keys, never to eval strings, so
/// the independent path and the oracle path share one vocabulary.
///
/// <para>VALUE keys mirror baseline fact keys (so <see cref="Check"/> can ask the baseline and
/// the oracle for the same key). ADDRESS keys name a struct/component whose live heap address the
/// oracle can hand back, enabling exact offset derivation in a probe's Discover path.</para>
/// </summary>
public static class OracleKeys
{
    /// <summary>key -> ExileAPI expression returning the truth VALUE (printed as a string).</summary>
    public static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["character.hp"]    = "Player.GetComponent<Life>().CurHP",
        ["character.mana"]  = "Player.GetComponent<Life>().CurMana",
        ["character.es"]    = "Player.GetComponent<Life>().CurES",
        ["character.maxhp"] = "Player.GetComponent<Life>().MaxHP",
        ["character.level"] = "(int)Player.GetComponent<Player>().Level",
        ["character.name"]  = "Player.GetComponent<Player>().PlayerName",
        ["area.level"]      = "(int)IngameState.Data.CurrentArea.AreaLevel",
        ["area.hash"]       = "(uint)IngameState.Data.CurrentAreaHash",
        ["league"]          = "IngameState.ServerData.League",
        ["camera.width"]    = "IngameState.Camera.Width",
        ["camera.height"]   = "IngameState.Camera.Height",
        ["camera.zfar"]     = "IngameState.Camera.ZFar",
        ["serverdata.monsterlevel"]      = "(int)IngameState.ServerData.MonsterLevel",
        ["serverdata.monstersremaining"] = "(int)IngameState.ServerData.MonstersRemaining",
        // ServerPlayerData (passive/skill-point read; checklist S-05)
        ["serverdata.player.level"]   = "IngameState.ServerData.PlayerInformation.Level",
        ["serverdata.player.class"]   = "(int)IngameState.ServerData.PlayerInformation.Class",
        ["serverdata.passive.free"]   = "IngameState.ServerData.PlayerInformation.FreePassiveSkillPointsLeft",
        ["serverdata.passive.quest"]  = "IngameState.ServerData.PlayerInformation.QuestPassiveSkillPoints",
        ["serverdata.passive.refund"] = "IngameState.ServerData.PlayerInformation.PassiveRefundPointsLeft",
        ["serverdata.asc.total"]      = "IngameState.ServerData.PlayerInformation.TotalAscendencyPoints",
        ["serverdata.asc.spent"]      = "IngameState.ServerData.PlayerInformation.SpentAscendencyPoints",
        ["terrain.bytesperrow"]          = "IngameState.Data.Terrain.BytesPerRow",
        // volatile (oracle-exact when present; otherwise range-checked). Not captured to baseline.
        ["player.grid.x"]    = "(int)Player.GridPosNum.X",
        ["player.grid.y"]    = "(int)Player.GridPosNum.Y",
        ["actor.actionid"]   = "(int)Player.GetComponent<Actor>().ActionId",
        ["actor.animationid"]= "(int)Player.GetComponent<Actor>().AnimationId",
        ["buffs.count"]      = "Player.Buffs.Count",
        ["serverdata.latency"] = "(int)IngameState.ServerData.Latency",
        ["camera.w2s.x"]     = "IngameState.Camera.WorldToScreen(Player.GetComponent<Render>().Pos).X",
        ["camera.w2s.y"]     = "IngameState.Camera.WorldToScreen(Player.GetComponent<Render>().Pos).Y",
        // inventory/stash (state-gated: only meaningful with the panels open)
        ["inventory.itemcount"] = "IngameState.IngameUi.InventoryPanel[ExileCore.Shared.Enums.InventoryIndex.PlayerInventory].ItemCount",
        ["stash.total"]      = "IngameState.IngameUi.StashElement.TotalStashes",
        ["stash.index"]      = "IngameState.IngameUi.StashElement.IndexVisibleStash",
    };

    /// <summary>
    /// The subset of <see cref="Values"/> stable enough to freeze into a baseline. Volatile values
    /// (HP, grid, action/animation ids, latency, monsters-remaining, world-to-screen) are excluded —
    /// they're checked live via <see cref="Check.Live"/>, not against a frozen fact.
    /// </summary>
    public static readonly string[] Stable =
    [
        "character.maxhp", "character.level", "character.name",
        "area.level", "area.hash", "league",
        "camera.width", "camera.height", "camera.zfar",
        "serverdata.monsterlevel", "terrain.bytesperrow",
    ];

    /// <summary>key -> ExileAPI expression returning a heap ADDRESS as an uppercase hex string.</summary>
    public static readonly IReadOnlyDictionary<string, string> Addresses = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ingameState"] = "((long)IngameState.Address).ToString(\"X\")",
        ["ingameData"]  = "((long)IngameState.Data.Address).ToString(\"X\")",
        ["serverData"]  = "((long)IngameState.ServerData.Address).ToString(\"X\")",
        ["camera"]      = "((long)IngameState.Camera.Address).ToString(\"X\")",
        ["ingameUi"]    = "((long)IngameState.IngameUi.Address).ToString(\"X\")",
        ["uiRoot"]      = "((long)IngameState.UIRoot.Address).ToString(\"X\")",
        ["currentArea"] = "((long)IngameState.Data.CurrentArea.Address).ToString(\"X\")",
        ["entityList"]  = "((long)IngameState.Data.EntityList.Address).ToString(\"X\")",
        ["player"]      = "((long)Player.Address).ToString(\"X\")",
        ["player.life"] = "((long)Player.GetComponent<Life>().Address).ToString(\"X\")",
        ["player.render"]     = "((long)Player.GetComponent<Render>().Address).ToString(\"X\")",
        ["player.positioned"] = "((long)Player.GetComponent<Positioned>().Address).ToString(\"X\")",
        ["player.actor"]      = "((long)Player.GetComponent<Actor>().Address).ToString(\"X\")",
        ["player.playercomp"] = "((long)Player.GetComponent<Player>().Address).ToString(\"X\")",
        // UI panels (Element addresses; present even when the panel is hidden)
        ["ui.gameui"]         = "((long)IngameState.IngameUi.GameUI.Address).ToString(\"X\")",
        ["ui.skillbar"]       = "((long)IngameState.IngameUi.SkillBar.Address).ToString(\"X\")",
        ["ui.hiddenskillbar"] = "((long)IngameState.IngameUi.HiddenSkillBar.Address).ToString(\"X\")",
        ["ui.questtracker"]   = "((long)IngameState.IngameUi.QuestTracker.Address).ToString(\"X\")",
        ["ui.openleftpanel"]  = "((long)IngameState.IngameUi.OpenLeftPanel.Address).ToString(\"X\")",
        ["ui.openrightpanel"] = "((long)IngameState.IngameUi.OpenRightPanel.Address).ToString(\"X\")",
        ["ui.inventorypanel"] = "((long)IngameState.IngameUi.InventoryPanel.Address).ToString(\"X\")",
        ["ui.stashelement"]   = "((long)IngameState.IngameUi.StashElement.Address).ToString(\"X\")",
        ["ui.mapsideui"]      = "((long)IngameState.IngameUi.MapSideUI.Address).ToString(\"X\")",
        ["inventory.player"]  = "((long)IngameState.IngameUi.InventoryPanel[ExileCore.Shared.Enums.InventoryIndex.PlayerInventory].Address).ToString(\"X\")",
        ["stash.visible"]     = "((long)IngameState.IngameUi.StashElement.VisibleStash.Address).ToString(\"X\")",
    };
}
