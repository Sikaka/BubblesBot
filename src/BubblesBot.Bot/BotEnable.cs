using BubblesBot.Bot.Overlay.Native;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Diagnostics;

namespace BubblesBot.Bot;

/// <summary>
/// Per-tick gating for bot actions. Three independent layers:
///
/// 1. <b>Foreground</b>: PoE must be the OS-level foreground window. Alt-tabbing kills
///    actions immediately so clicks don't land on whatever you switched to.
///
/// 2. <b>UserEnabled</b>: persisted in <see cref="BotSettings.BotActive"/>, so it survives
///    restarts. Toggleable in-game via Insert (edge-detected) or via the web UI.
///
/// 3. <b>LootKeyHeld</b>: the configured loot hotkey is currently held. Polled live, no edge
///    detection — release the key and looting stops the same tick.
/// </summary>
public sealed class BotEnable
{
    private const int VK_INSERT = 0x2D;

    private readonly SettingsStore _settings;

    private bool _insertWasDown; // edge-detect
    private bool? _lastReportedEnabled;

    public BotEnable(SettingsStore settings) { _settings = settings; }

    public bool UserEnabled       => _settings.Current.BotActive;
    public bool ForegroundOk      { get; private set; }
    public bool GateAvailable     { get; private set; }
    public bool GameStateAllowsInput { get; private set; }
    public bool LootKeyHeld       { get; private set; }
    public bool ShouldAct         => UserEnabled && ForegroundOk && GateAvailable && GameStateAllowsInput;
    public bool ShouldLoot        => ShouldAct && LootKeyHeld;

    /// <summary>
    /// Human-readable bot state. Mode-aware: in Loot mode it shows hold-key prompts; in
    /// other modes it just reflects armed/paused/disarmed since they don't gate on a hotkey.
    /// </summary>
    public string StateLabel
    {
        get
        {
            if (!UserEnabled) return "DISARMED (Insert / web UI to arm)";
            if (!GateAvailable) return "DISARMED — game-state gate unavailable";
            if (!GameStateAllowsInput) return "ARMED — paused (game is not in-world)";
            if (!ForegroundOk) return "ARMED — paused (PoE not focused)";
            if (_settings.Current.ActiveMode == 0)
                return LootKeyHeld ? "ARMED — LOOTING" : "ARMED — hold loot key";
            return "ARMED — RUNNING";
        }
    }

    /// <summary>Drive once per tick.</summary>
    public void Tick(nint gameHwnd, bool gateAvailable, bool gameStateAllowsInput)
    {
        ForegroundOk = OverlayNative.IsForeground(gameHwnd);
        GateAvailable = gateAvailable;
        GameStateAllowsInput = gameStateAllowsInput;

        // Missing gate patterns are an unsafe startup condition, not a temporary loading
        // state. Clear a persisted arm bit so input cannot resume later without user intent.
        var changeSource = "observed";
        if (!GateAvailable && UserEnabled)
        {
            _settings.Mutate(s => s.BotActive = false);
            changeSource = "gate-unavailable";
        }

        // Insert toggles BotActive. Edge-detect so holding the key doesn't oscillate.
        var insertDown = (OverlayNative.GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;
        if (insertDown && !_insertWasDown && GateAvailable)
        {
            _settings.Mutate(s => s.BotActive = !s.BotActive);
            changeSource = "insert-hotkey";
        }
        _insertWasDown = insertDown;

        if (_lastReportedEnabled != UserEnabled)
        {
            EventLog.Emit("automation",
                UserEnabled ? "automation.armed" : "automation.disarmed",
                UserEnabled ? EventSeverity.Warning : EventSeverity.Info,
                UserEnabled ? $"automation armed ({changeSource})" : $"automation disarmed ({changeSource})",
                new Dictionary<string, object?>
                {
                    ["source"] = changeSource,
                    ["foreground"] = ForegroundOk,
                    ["gateAvailable"] = GateAvailable,
                    ["gameStateAllowsInput"] = GameStateAllowsInput,
                });
            _lastReportedEnabled = UserEnabled;
        }

        // Loot hotkey: live held-state of whatever VK the user has bound.
        var lootVk = _settings.Current.Loot.HotkeyVk;
        LootKeyHeld = lootVk != 0 && (OverlayNative.GetAsyncKeyState(lootVk) & 0x8000) != 0;
    }
}
