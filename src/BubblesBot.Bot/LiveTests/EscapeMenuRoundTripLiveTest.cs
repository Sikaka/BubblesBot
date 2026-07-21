using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Open, inspect, and close the Escape menu without selecting a logout action.</summary>
public sealed class EscapeMenuRoundTripLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private const string CharacterSelectionAction = "EXIT TO CHARACTER SELECTION";
    private const string ResumeAction = "RESUME GAME";
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "PurchaseWindow", "NpcDialog" };

    public string Id => "U-10-escape-menu-roundtrip";
    public string Name => "Escape menu discovery round trip";
    public string Description => "Closes any prepared NPC panels, proves a clean UI baseline, opens the system menu through the exact HUD Menu control, reads its actions, and restores the starting UI state.";
    public string ManualSetup => "Stand alive in a safe town/hideout. Either leave all UI windows closed, or stand beside Nessa with Purchase Items open on page 2. Hold no item and leave PoE focused. This test does not select logout.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var preparedSnapshot = context.Snapshot();
        var preparedShop = VendorPurchaseView.Read(preparedSnapshot.Reader, preparedSnapshot.IngameStateAddress);
        var restorePreparedShop = preparedShop.IsOpen;
        var preparedFingerprint = restorePreparedShop ? Fingerprint(preparedShop) : string.Empty;
        context.Observe("prepared UI baseline", restorePreparedShop
            ? $"Nessa shop open; visible={preparedShop.Offers.Count(x => x.IsVisible)}"
            : "no purchase window; terminal state will remain panel-free");
        if (!await CloseNpcPanelsAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("could not reach a panel-free safe-town baseline", "BaselineCloseFailed");

        var clean = context.Snapshot();
        var cleanDialog = NpcDialogView.Read(clean.Reader, clean.IngameStateAddress);
        var cleanShop = VendorPurchaseView.Read(clean.Reader, clean.IngameStateAddress);
        var unexpectedPanels = clean.OpenPanels.BlockingOpen();
        context.Check(!cleanShop.IsOpen && !cleanDialog.IsOpen,
            "shop and NPC dialog closed", $"shop={cleanShop.IsOpen} dialog={cleanDialog.IsOpen}");
        context.Check(!clean.OpenPanels.IsOpen("InventoryPanel"),
            "inventory closed before Escape menu", $"open=[{string.Join(", ", clean.OpenPanels.Open)}]");
        context.Check(unexpectedPanels.Count == 0,
            "no tracked blocking UI before Escape menu",
            unexpectedPanels.Count == 0 ? "none" : string.Join(", ", unexpectedPanels));
        if (cleanShop.IsOpen || cleanDialog.IsOpen || clean.OpenPanels.IsOpen("InventoryPanel") || unexpectedPanels.Count > 0)
            return LiveTestCaseResult.Blocked("a tracked UI window remains open before Escape", "CleanUiBaselineMissing");

        var before = ReadText(context);
        context.Check(before.FindExact(CharacterSelectionAction).Count == 0,
            "character-selection action absent before Escape", $"visibleText={before.Elements.Count}");

        var menuOpeners = before.FindExact("Menu")
            .Where(x => x.Rect is { Width: > 0, Height: > 0 } rect
                && rect.IntersectsWindow(clean.Window.Width, clean.Window.Height)
                && rect.CenterY > clean.Window.Height * 0.8f)
            .ToArray();
        context.Check(menuOpeners.Length == 1, "HUD Menu control identity", $"matches={menuOpeners.Length}");
        if (menuOpeners.Length != 1 || menuOpeners[0].Rect is not { } openerRect)
            return LiveTestCaseResult.Fail("one exact on-screen HUD Menu control was not readable", "MenuOpenerMissing");
        var openerPoint = clean.Window.ToScreen(openerRect.CenterX, openerRect.CenterY);

        var open = await context.VerifiedClickAsync(
            openerPoint.X,
            openerPoint.Y,
            ClickIntent.InteractUi,
            "open system menu through exact HUD Menu control",
            () => ReadText(context).FindExact(CharacterSelectionAction).Count > 0,
            timeoutMs: 2_000,
            cancellationToken);
        if (open != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Escape did not expose a character-selection action", "EscapeMenuOpenFailed");
        if (!await context.WaitForInputIdleAsync("after Escape menu open", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after opening Escape menu", "InputSettleFailed");

        var menu = ReadText(context);
        var characterActions = menu.FindExact(CharacterSelectionAction)
            .Where(x => x.Rect is { Width: > 0, Height: > 0 })
            .ToArray();
        context.Observe("Escape menu visible text",
            string.Join(" | ", menu.Elements.Select(x => OneLine(x.Text)).Distinct(StringComparer.OrdinalIgnoreCase).Take(80)),
            new Dictionary<string, object?>
            {
                ["root"] = $"0x{(long)menu.Root:X}",
                ["visited"] = menu.Visited,
                ["textElements"] = menu.Elements.Count,
                ["characterSelectionMatches"] = characterActions.Select(x => new
                {
                    element = $"0x{(long)x.Element:X}",
                    x.TreePath,
                    x.Text,
                    rect = x.Rect?.ToString(),
                    x.ChildCount,
                }).ToArray(),
            });
        context.Check(characterActions.Length == 1,
            "unique character-selection action", $"matches={characterActions.Length}");
        if (characterActions.Length != 1)
            return LiveTestCaseResult.Fail("Escape menu did not expose one unambiguous character-selection action", "EscapeMenuShapeMismatch");

        var expectedActions = new[]
        {
            ResumeAction,
            "OPTIONS",
            "MICROTRANSACTION SHOP",
            "EXIT TO LOG IN SCREEN",
            CharacterSelectionAction,
            "EXIT PATH OF EXILE",
        };
        foreach (var expectedAction in expectedActions)
            context.Check(menu.FindExact(expectedAction).Count == 1,
                $"system menu action {expectedAction}", $"matches={menu.FindExact(expectedAction).Count}");
        if (expectedActions.Any(x => menu.FindExact(x).Count != 1))
            return LiveTestCaseResult.Fail("system menu action set did not match the visual oracle", "EscapeMenuShapeMismatch");

        var action = characterActions[0];
        context.Check(action.Rect is { } rect && rect.IntersectsWindow(context.Snapshot().Window.Width, context.Snapshot().Window.Height),
            "character-selection action geometry",
            $"text='{OneLine(action.Text)}' element=0x{(long)action.Element:X} path={action.TreePath} rect={action.Rect}");

        var resume = menu.FindExact(ResumeAction)
            .SingleOrDefault(x => x.Rect is { Width: > 0, Height: > 0 });
        context.Check(resume?.Rect is not null, "Resume Game target geometry", $"element=0x{(long)(resume?.Element ?? 0):X} rect={resume?.Rect}");
        if (resume?.Rect is not { } resumeRect)
            return LiveTestCaseResult.Fail("Resume Game target was not readable", "ResumeTargetMissing");
        var resumePoint = context.Snapshot().Window.ToScreen(resumeRect.CenterX, resumeRect.CenterY);
        var close = await context.VerifiedClickAsync(
            resumePoint.X,
            resumePoint.Y,
            ClickIntent.InteractUi,
            "close system menu through exact Resume Game control",
            () => ReadText(context).FindExact(CharacterSelectionAction).Count == 0,
            timeoutMs: 2_000,
            cancellationToken);
        if (close != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Escape menu did not close", "EscapeMenuCloseFailed");
        if (!await context.WaitForInputIdleAsync("after Escape menu close", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after closing Escape menu", "InputSettleFailed");

        if (restorePreparedShop)
        {
            var restore = await new NpcShopRoundTripLiveTest().RunAsync(context, cancellationToken);
            if (restore.Outcome != LiveTestOutcome.Passed)
                return LiveTestCaseResult.Fail($"Escape menu passed but Nessa restoration failed: {restore.Summary}", "RestoreFailed");
            var restoredSnapshot = context.Snapshot();
            var restored = VendorPurchaseView.Read(restoredSnapshot.Reader, restoredSnapshot.IngameStateAddress);
            context.Check(Fingerprint(restored) == preparedFingerprint,
                "prepared Nessa shop restored", $"visible={restored.Offers.Count(x => x.IsVisible)}");
            if (Fingerprint(restored) != preparedFingerprint)
                return LiveTestCaseResult.Fail("Nessa shop restored with different content/page", "RestoreFailed");
        }
        else
        {
            var terminal = context.Snapshot();
            context.Check(!VendorPurchaseView.Read(terminal.Reader, terminal.IngameStateAddress).IsOpen
                && !NpcDialogView.Read(terminal.Reader, terminal.IngameStateAddress).IsOpen,
                "panel-free terminal state", $"open=[{string.Join(", ", terminal.OpenPanels.Open)}]");
        }

        return LiveTestCaseResult.Pass(
            $"Escape menu exposed exact action '{OneLine(action.Text)}', closed without logout, and restored the prepared UI state",
            "CompletedAndRestored");
    }

    private static async Task<bool> CloseNpcPanelsAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var snapshot = context.Snapshot();
            var shopOpen = VendorPurchaseView.Read(snapshot.Reader, snapshot.IngameStateAddress).IsOpen;
            var dialog = NpcDialogView.Read(snapshot.Reader, snapshot.IngameStateAddress);
            var dialogOpen = dialog.IsOpen;
            if (!shopOpen && !dialogOpen) return true;
            ActionOutcome close;
            if (shopOpen)
            {
                close = await context.VerifiedTapKeyAsync(
                    EscapeVk,
                    ClickIntent.InteractUi,
                    "close Purchase Items before Escape-menu research",
                    () =>
                    {
                        var live = context.Snapshot();
                        return !VendorPurchaseView.Read(live.Reader, live.IngameStateAddress).IsOpen;
                    },
                    timeoutMs: 2_000,
                    cancellationToken);
            }
            else
            {
                var goodbye = dialog.FindExact("Goodbye")
                    .Where(x => x.Rect is { Width: > 0, Height: > 0 })
                    .ToArray();
                context.Check(goodbye.Length == 1, "Nessa Goodbye identity", $"matches={goodbye.Length}");
                if (goodbye.Length != 1 || goodbye[0].Rect is not { } rect) return false;
                var screen = snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);
                close = await context.VerifiedClickAsync(
                    screen.X,
                    screen.Y,
                    ClickIntent.InteractUi,
                    "select Nessa Goodbye before Escape-menu research",
                    () =>
                    {
                        var live = context.Snapshot();
                        return !NpcDialogView.Read(live.Reader, live.IngameStateAddress).IsOpen;
                    },
                    timeoutMs: 2_000,
                    cancellationToken);
            }
            if (close != ActionOutcome.Confirmed
                || !await context.WaitForInputIdleAsync("after NPC panel close", 1_500, cancellationToken))
                return false;
        }
        return false;
    }

    private static VisibleUiTextView ReadText(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VisibleUiTextView.ReadInGame(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static string Fingerprint(VendorPurchaseView view)
        => string.Join('|', view.Offers.Where(x => x.IsVisible)
            .OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{x.Metadata}:{x.BaseName}:"
                + $"{x.RequiredAttributes.Strength},{x.RequiredAttributes.Dexterity},{x.RequiredAttributes.Intelligence}"));

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', '|').Trim();
}
