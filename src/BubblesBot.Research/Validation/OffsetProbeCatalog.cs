using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation;

/// <summary>
/// The full registry of <see cref="Probe"/>s that <c>--sweep-offsets</c> runs. Add a probe
/// for any field the bot reads — once a probe exists, the per-patch sweep automatically
/// catches drift.
///
/// <para><b>Coverage philosophy.</b> Probe everything we read in code. Skip fields that
/// nothing consumes — they'll get probed when they're needed. Probes for "obviously stable"
/// constants (single-byte enum offsets within a struct) are still valuable as canaries.</para>
/// </summary>
public static class OffsetProbeCatalog
{
    /// <summary>
    /// POEMCP eval expressions for the parent struct addresses. Run once per sweep, cached.
    /// Add new bases here as new probe categories appear.
    /// </summary>
    public static readonly (string Key, string Expression)[] BaseAddresses =
    {
        ("ingameState",  "((long)IngameState.Address).ToString(\"X\")"),
        ("ingameData",   "((long)IngameState.Data.Address).ToString(\"X\")"),
        ("serverData",   "((long)IngameState.ServerData.Address).ToString(\"X\")"),
        ("camera",       "((long)IngameState.Camera.Address).ToString(\"X\")"),
        ("ingameUi",     "((long)IngameState.IngameUi.Address).ToString(\"X\")"),
        ("uiRoot",       "((long)IngameState.UIRoot.Address).ToString(\"X\")"),
        ("playerEntity", "((long)Player.Address).ToString(\"X\")"),
        ("playerLife",   "((long)Player.GetComponent<Life>().Address).ToString(\"X\")"),
        ("playerRender", "((long)Player.GetComponent<Render>().Address).ToString(\"X\")"),
        ("playerPositioned", "((long)Player.GetComponent<Positioned>().Address).ToString(\"X\")"),
        ("playerActor",  "((long)Player.GetComponent<Actor>().Address).ToString(\"X\")"),
        ("playerBuffs",  "((long)Player.GetComponent<ExileCore.PoEMemory.Components.Buffs>().Address).ToString(\"X\")"),
        ("playerPlayerComp", "((long)Player.GetComponent<Player>().Address).ToString(\"X\")"),
        ("playerInformation", "((long)IngameState.ServerData.PlayerInformation.Address).ToString(\"X\")"),
    };

    /// <summary>The probe table. Categories group fields by parent struct.</summary>
    public static readonly Probe[] Probes =
    {
        // ── IngameState ─────────────────────────────────────────────────
        new("IngameState", "Data",            "ingameState",  KnownOffsets.IngameState.Data,            ProbeKind.Pointer, "((long)IngameState.Data.Address).ToString(\"X\")"),
        new("IngameState", "Camera",          "ingameState",  KnownOffsets.IngameState.Camera,          ProbeKind.Pointer, "((long)IngameState.Camera.Address).ToString(\"X\")"),
        new("IngameState", "IngameUi",        "ingameState",  KnownOffsets.IngameState.IngameUi,        ProbeKind.Pointer, "((long)IngameState.IngameUi.Address).ToString(\"X\")"),
        new("IngameState", "UIRoot",          "ingameState",  KnownOffsets.IngameState.UIRoot,          ProbeKind.Pointer, "((long)IngameState.UIRoot.Address).ToString(\"X\")"),
        // EntityLabelMap is a long pointer not an Element wrapper in POEMCP — skip wrapper-style; sanity-check via reachability rather than equality (no truth available).

        // ── IngameData ──────────────────────────────────────────────────
        new("IngameData",  "ServerData",      "ingameData",   KnownOffsets.IngameData.ServerData,       ProbeKind.Pointer, "((long)IngameState.ServerData.Address).ToString(\"X\")"),
        new("IngameData",  "LocalPlayer",     "ingameData",   KnownOffsets.IngameData.LocalPlayer,      ProbeKind.Pointer, "((long)Player.Address).ToString(\"X\")"),
        new("IngameData",  "EntityList",      "ingameData",   KnownOffsets.IngameData.EntityList,       ProbeKind.Pointer, "((long)IngameState.Data.EntityList.Address).ToString(\"X\")"),
        new("IngameData",  "CurrentArea",     "ingameData",   KnownOffsets.IngameData.CurrentArea,      ProbeKind.Pointer, "((long)IngameState.Data.CurrentArea.Address).ToString(\"X\")"),
        new("IngameData",  "CurrentAreaHash", "ingameData",   KnownOffsets.IngameData.CurrentAreaHash,  ProbeKind.Int32,   "(int)IngameState.Data.CurrentAreaHash"),
        new("IngameData",  "CurrentAreaLevel","ingameData",   KnownOffsets.IngameData.CurrentAreaLevel, ProbeKind.Byte,    "(int)IngameState.Data.CurrentArea.AreaLevel"),
        new("IngameData",  "TgtArray.first",  "ingameData",   KnownOffsets.IngameData.TgtArray,         ProbeKind.Pointer, "((long)IngameState.Data.Terrain.TgtArray.First).ToString(\"X\")"),
        new("IngameData",  "TerrainBytesPerRow", "ingameData", KnownOffsets.IngameData.TerrainBytesPerRow, ProbeKind.Int32, "IngameState.Data.Terrain.BytesPerRow"),

        // ── ServerData ──────────────────────────────────────────────────
        // Latency is volatile — mismatch by ±2 is normal frame jitter; we keep this probe as a sanity check.
        new("ServerData",  "MonsterLevel",    "serverData",   KnownOffsets.ServerData.MonsterLevel,     ProbeKind.Byte,    "(int)IngameState.ServerData.MonsterLevel"),
        new("ServerData",  "MonstersRemaining","serverData",  KnownOffsets.ServerData.MonstersRemaining,ProbeKind.Byte,    "(int)IngameState.ServerData.MonstersRemaining"),
        new("ServerData",  "PlayerRelatedData","serverData",  KnownOffsets.ServerData.PlayerRelatedData,ProbeKind.Pointer, "((long)IngameState.ServerData.PlayerInformation.Address).ToString(\"X\")"),

        // ── ServerPlayerData (passive/skill-point read; checklist S-05) ──
        // Base = *(ServerData + PlayerRelatedData). Only oracle-VALID fields are swept: Class + Level
        // (non-zero anchors) and PassiveRefundPoints (ExileCore reads 0x278 correctly, matched at 5).
        // FreePassiveSkillPointsLeft is DELIBERATELY NOT swept: ExileCore's field is stale this patch
        // (its 0x280 reads 0 while the truth is 0x2D0=7), so it is not a valid oracle — the value is
        // proven by the differential allocation LiveTest instead. QuestPassive + ascendancy fields are
        // also omitted: their offsets are unconfirmed and read 0, so a sweep would false-pass (0==0).
        new("ServerPlayerData", "PlayerClass",         "playerInformation", KnownOffsets.ServerPlayerData.PlayerClass,             ProbeKind.Byte,  "(int)IngameState.ServerData.PlayerInformation.Class"),
        new("ServerPlayerData", "CharacterLevel",      "playerInformation", KnownOffsets.ServerPlayerData.CharacterLevel,          ProbeKind.Int32, "IngameState.ServerData.PlayerInformation.Level"),
        new("ServerPlayerData", "PassiveRefundPoints", "playerInformation", KnownOffsets.ServerPlayerData.PassiveRefundPointsLeft, ProbeKind.Int32, "IngameState.ServerData.PlayerInformation.PassiveRefundPointsLeft"),

        // ── Camera ──────────────────────────────────────────────────────
        new("Camera",      "Width",           "camera",       KnownOffsets.Camera.Width,                ProbeKind.Int32,   "IngameState.Camera.Width"),
        new("Camera",      "Height",          "camera",       KnownOffsets.Camera.Height,               ProbeKind.Int32,   "IngameState.Camera.Height"),
        new("Camera",      "ZFar",            "camera",       KnownOffsets.Camera.ZFar,                 ProbeKind.Float,   "IngameState.Camera.ZFar"),

        // ── IngameUi top-level panels ───────────────────────────────────
        new("IngameUi",    "GameUI",          "ingameUi",     KnownOffsets.IngameUiElements.GameUI,           ProbeKind.Pointer, "((long)IngameState.IngameUi.GameUI.Address).ToString(\"X\")"),
        new("IngameUi",    "SkillBar",        "ingameUi",     KnownOffsets.IngameUiElements.SkillBar,         ProbeKind.Pointer, "((long)IngameState.IngameUi.SkillBar.Address).ToString(\"X\")"),
        new("IngameUi",    "HiddenSkillBar",  "ingameUi",     KnownOffsets.IngameUiElements.HiddenSkillBar,   ProbeKind.Pointer, "((long)IngameState.IngameUi.HiddenSkillBar.Address).ToString(\"X\")"),
        // ChatBox: 0x528 returns a non-null pointer but it's NOT the ChatBox — sweep 2026-05-06
        // confirmed it's not under IngameUi at any direct offset 0..0x2000. ExileCore likely
        // walks the UI tree to find ChatBox. Marked as known-drift; not currently used by bot.
        // Re-enable this probe with the right offset/path once we wire chat-aware behavior.
        new("IngameUi",    "QuestTracker",    "ingameUi",     KnownOffsets.IngameUiElements.QuestTracker,     ProbeKind.Pointer, "((long)IngameState.IngameUi.QuestTracker.Address).ToString(\"X\")"),
        new("IngameUi",    "OpenLeftPanel",   "ingameUi",     KnownOffsets.IngameUiElements.OpenLeftPanel,    ProbeKind.Pointer, "((long)IngameState.IngameUi.OpenLeftPanel.Address).ToString(\"X\")"),
        new("IngameUi",    "OpenRightPanel",  "ingameUi",     KnownOffsets.IngameUiElements.OpenRightPanel,   ProbeKind.Pointer, "((long)IngameState.IngameUi.OpenRightPanel.Address).ToString(\"X\")"),
        new("IngameUi",    "InventoryPanel",  "ingameUi",     KnownOffsets.IngameUiElements.InventoryPanel,   ProbeKind.Pointer, "((long)IngameState.IngameUi.InventoryPanel.Address).ToString(\"X\")"),
        new("IngameUi",    "StashElement",    "ingameUi",     KnownOffsets.IngameUiElements.StashElement,     ProbeKind.Pointer, "((long)IngameState.IngameUi.StashElement.Address).ToString(\"X\")"),
        new("IngameUi",    "MapSideUI",       "ingameUi",     KnownOffsets.IngameUiElements.MapSideUI,        ProbeKind.Pointer, "((long)IngameState.IngameUi.MapSideUI.Address).ToString(\"X\")"),

        // ── Player component ────────────────────────────────────────────
        new("PlayerComp",  "Level",           "playerPlayerComp", KnownOffsets.PlayerComponent.Level,         ProbeKind.Byte,    "(int)Player.GetComponent<Player>().Level"),

        // ── Buffs component ─────────────────────────────────────────────
        // BuffsComponent.Buffs (NativePtrArray) is end-to-end validated separately by the
        // BuffsTests harness (9/9 buff names matched POEMCP truth). No probe needed here.

        // ── Element fields (validated by ElementTreeTests already, kept here as sweep canaries) ──
        // Element offsets are read against the Player UI element which always exists.

        // ── Positioned / Render / Life ─────────────────────────────────
        new("Positioned",  "GridPosition",    "playerPositioned", KnownOffsets.PositionedComponent.GridPosition, ProbeKind.Int32, "(int)Player.GridPosNum.X"),  // first int of Vector2i
        new("Render",      "Pos.X",           "playerRender", KnownOffsets.RenderComponent.Pos,               ProbeKind.Float,   "Player.GetComponent<Render>().Pos.X"),
        new("Life",        "Health.Current",  "playerLife",   KnownOffsets.LifeComponent.Health + 0x30,       ProbeKind.Int32,   "Player.GetComponent<Life>().CurHP"),
        new("Life",        "Health.Max",      "playerLife",   KnownOffsets.LifeComponent.Health + 0x2C,       ProbeKind.Int32,   "Player.GetComponent<Life>().MaxHP"),

        // ── Camera projection matrix (validated by CameraWorldToScreenTest end-to-end) ──
        // Per-element probes are awkward in eval syntax; the matrix's correctness is proven
        // by the projection oracle test which compares our projected pixel coords to POEMCP's.

        // ── Tile data (validated 2026-05-06 via end-to-end waypoint detection) ──
        // Probes here would be circular against POEMCP (POEMCP doesn't expose individual tile
        // structs cleanly). Coverage is via the OracleTest pattern in TerrainGridTests.
    };
}
