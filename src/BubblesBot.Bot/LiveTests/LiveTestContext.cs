using BubblesBot.Bot.Input;
using BubblesBot.Bot.Overlay.Native;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

public sealed class LiveTestContext
{
    private readonly MemoryReader _reader;
    private readonly nint _ingameStateAddress;
    private readonly nint _gameWindow;
    private readonly GameStateView _gameState;
    private readonly LiveTestOptions _options;
    private readonly LiveTestRecorder _recorder;
    private readonly GuardedLiveTestInput _input;
    private readonly CancellationToken _runToken;
    private IReadOnlySet<GameStateKind> _allowedInputStates = new HashSet<GameStateKind> { GameStateKind.InGame };
    private IReadOnlySet<string> _allowedBlockingPanels = new HashSet<string>(StringComparer.Ordinal);
    private uint? _expectedAreaHash;
    private bool _inputBlocked;

    public int PassedChecks { get; private set; }
    public int FailedChecks { get; private set; }
    public LiveTestPhase Phase => _options.Phase!.Value;
    public bool Commit => _options.Commit;
    public string? ExpectedReward => _options.ExpectedReward;
    public string EvidenceDirectory => _recorder.EvidenceDirectory;
    public GameStateKind GameState => _gameState.ReadKind();
    public WindowInfo Window => ReadWindow();

    internal LiveTestContext(
        MemoryReader reader,
        nint ingameDataAddress,
        nint ingameStateAddress,
        nint gameWindow,
        GameStateView gameState,
        LiveTestOptions options,
        LiveTestRecorder recorder,
        InputRouter input,
        CancellationToken runToken)
    {
        _reader = reader;
        _ingameStateAddress = ingameStateAddress;
        _gameWindow = gameWindow;
        _gameState = gameState;
        _options = options;
        _recorder = recorder;
        _runToken = runToken;
        _expectedAreaHash = options.ExpectedAreaHash;
        IngameDataAddress = ingameDataAddress;
        _input = new GuardedLiveTestInput(input, CanDispatchInput, OnInputBlocked);
    }

    public nint IngameDataAddress { get; private set; }

    public GameSnapshot Snapshot()
    {
        RefreshIngameData();
        return new GameSnapshot(_reader, IngameDataAddress, _ingameStateAddress, ReadWindow());
    }

    public bool Check(bool condition, string label, string detail)
    {
        if (condition) PassedChecks++; else FailedChecks++;
        var status = condition ? "PASS" : "FAIL";
        Console.WriteLine($"  [{status}] {label} — {detail}");
        _recorder.Record("check", label, condition, detail);
        return condition;
    }

    public void Observe(string label, string detail, IReadOnlyDictionary<string, object?>? data = null)
    {
        Console.WriteLine($"  [OBSERVE] {label} — {detail}");
        _recorder.Record("observation", label, null, detail, data);
    }

    public void SetAllowedInputStates(params GameStateKind[] states)
    {
        _allowedInputStates = new HashSet<GameStateKind>(states);
        Observe("allowed input states", string.Join(", ", states));
    }

    public void SetAllowedBlockingPanels(IReadOnlySet<string> panels)
    {
        _allowedBlockingPanels = new HashSet<string>(panels, StringComparer.Ordinal);
        Observe("allowed blocking panels", _allowedBlockingPanels.Count == 0
            ? "none"
            : string.Join(", ", _allowedBlockingPanels));
    }

    public void SetExpectedArea(uint? areaHash)
    {
        _expectedAreaHash = areaHash;
        Observe("expected area updated", areaHash is { } h ? $"0x{h:X8}" : "(any explicitly validated area)");
    }

    public async Task<ActionOutcome> VerifiedTapKeyAsync(
        int vk,
        ClickIntent intent,
        string description,
        Func<bool> postcondition,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var ticket = _input.VerifiedTapKey(vk, intent, description, postcondition, timeoutMs);
        return await AwaitVerifiedTicketAsync(ticket, description, cancellationToken);
    }

    public async Task<ActionOutcome> VerifiedTapScanCodeAsync(
        int scanCode,
        ClickIntent intent,
        string description,
        Func<bool> postcondition,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var ticket = _input.VerifiedTapScanCode(
            scanCode, intent, description, postcondition, timeoutMs);
        return await AwaitVerifiedTicketAsync(ticket, description, cancellationToken);
    }

    public async Task<ActionOutcome> VerifiedClickAsync(
        int absoluteX,
        int absoluteY,
        ClickIntent intent,
        string description,
        Func<bool> postcondition,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var ticket = _input.Click(
            absoluteX, absoluteY, intent, description, postcondition, timeoutMs);
        return await AwaitVerifiedTicketAsync(ticket, description, cancellationToken);
    }

    public async Task<ActionOutcome> VerifiedRightClickAsync(
        int absoluteX,
        int absoluteY,
        ClickIntent intent,
        string description,
        Func<bool> postcondition,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var ticket = _input.RightClick(
            absoluteX, absoluteY, intent, description, postcondition, timeoutMs);
        return await AwaitVerifiedTicketAsync(ticket, description, cancellationToken);
    }

    public async Task<ActionOutcome> VerifiedModifierClickAsync(
        int absoluteX,
        int absoluteY,
        int[] modifiers,
        ClickIntent intent,
        string description,
        Func<bool> postcondition,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var ticket = _input.ModifierClick(
            absoluteX, absoluteY, modifiers, intent, description, postcondition, timeoutMs);
        return await AwaitVerifiedTicketAsync(ticket, description, cancellationToken);
    }

    public async Task<bool> WaitUntilAsync(
        string description,
        Func<bool> predicate,
        int timeoutMs,
        CancellationToken cancellationToken,
        int pollMs = 25)
    {
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(timeoutMs / 1000d * System.Diagnostics.Stopwatch.Frequency);
        while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                Check(true, $"wait {description}", "condition reached");
                return true;
            }
            await Task.Delay(pollMs, cancellationToken);
        }

        var final = predicate();
        Check(final, $"wait {description}", final ? "condition reached at deadline" : $"timed out after {timeoutMs}ms");
        return final;
    }

    private async Task<ActionOutcome> AwaitVerifiedTicketAsync(
        InputTicket ticket,
        string description,
        CancellationToken cancellationToken)
    {
        if (!ticket.Accepted || ticket.Token is null)
        {
            Check(false, $"dispatch {description}", _inputBlocked ? "live-test safety gate blocked input" : "production input router suppressed input");
            return ActionOutcome.Cancelled;
        }

        Check(true, $"dispatch {description}", $"action #{ticket.Token.ActionId} accepted");
        while (!ticket.IsResolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runToken.ThrowIfCancellationRequested();
            _input.Tick();
            await Task.Delay(16, cancellationToken);
        }
        _input.Tick();

        var outcome = ticket.Token.Outcome;
        Check(outcome == ActionOutcome.Confirmed, $"verify {description}", $"outcome={outcome}");
        return outcome;
    }

    public async Task HoverAsync(int absoluteX, int absoluteY, int settleMs, CancellationToken cancellationToken)
    {
        _input.HoverAt(absoluteX, absoluteY, CursorPriority.CombatAim);
        _input.Tick();
        await Task.Delay(settleMs, cancellationToken);
    }

    public async Task<bool> WaitForInputIdleAsync(
        string description,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(timeoutMs / 1000d * System.Diagnostics.Stopwatch.Frequency);
        while (!_input.IsIdle && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runToken.ThrowIfCancellationRequested();
            _input.Tick();
            await Task.Delay(16, cancellationToken);
        }
        return Check(_input.IsIdle, $"input idle {description}", _input.GateState);
    }

    public void CancelAllInput() => _input.CancelAll();

    internal (bool Allowed, string Reason) CanDispatchInput()
    {
        if (_runToken.IsCancellationRequested) return (false, "run cancelled or timed out");
        if (!_options.Armed) return (false, "--arm was not supplied");
        if (_gameWindow == 0 || !OverlayNative.IsForeground(_gameWindow))
            return (false, "PoE is not the foreground window");

        var kind = _gameState.ReadKind();
        if (kind == GameStateKind.GateDisabled)
            return (false, "TheGame state gate is unavailable");
        if (!_allowedInputStates.Contains(kind))
            return (false, $"game state {kind} is not allowed for this step");

        if (kind == GameStateKind.InGame)
        {
            var snapshot = Snapshot();
            var player = snapshot.Player;
            var character = player?.CharacterName ?? string.Empty;
            if (!string.IsNullOrEmpty(_options.ExpectedCharacter)
                && !string.Equals(character, _options.ExpectedCharacter, StringComparison.Ordinal))
                return (false, "character changed: expected identity no longer matches");
            if (_expectedAreaHash is { } expected && snapshot.AreaHash != expected)
                return (false, $"area changed: expected 0x{expected:X8}, observed 0x{snapshot.AreaHash:X8}");
            if (player is null || player.Life.Max <= 0 || player.Life.Current <= 0
                || player.Life.Current > player.Life.Max)
                return (false, player is null
                    ? "player read became unavailable"
                    : $"player life became invalid: {player.Life.Current}/{player.Life.Max}");
            var blocking = snapshot.OpenPanels.BlockingOpen()
                .Where(x => !_allowedBlockingPanels.Contains(x)).ToArray();
            if (blocking.Length > 0)
                return (false, $"blocking panel appeared: {string.Join(", ", blocking)}");
        }

        return (true, "allowed");
    }

    private void OnInputBlocked(string reason)
    {
        _inputBlocked = true;
        Check(false, "input safety gate", reason);
        _recorder.Record("classification", "InputBlocked", false, reason);
    }

    private void RefreshIngameData()
    {
        if (_reader.TryReadStruct<nint>(_ingameStateAddress + KnownOffsets.IngameState.Data, out var liveData)
            && liveData != 0)
            IngameDataAddress = liveData;
    }

    private WindowInfo ReadWindow()
    {
        if (_gameWindow == 0 || !OverlayNative.GetWindowRect(_gameWindow, out var rect))
            return WindowInfo.Empty;
        return new WindowInfo(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}
