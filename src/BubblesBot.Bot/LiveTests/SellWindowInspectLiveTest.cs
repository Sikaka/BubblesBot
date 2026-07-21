using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only fingerprint of the prepared NPC sell window.</summary>
public sealed class SellWindowInspectLiveTest : ILiveTestCase
{
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { "SellWindow" };

    public string Id => "A-04-sell-window-inspect";
    public string Name => "Sell window inspection";
    public string Description => "Reads semantic sell regions, item entities, and accept/cancel controls without input.";
    public string ManualSetup => "Open an NPC Sell Items window and leave its offer empty.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => Allowed;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.Snapshot();
        var view = SellWindowView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        context.Check(view.IsOpen, "sell window", $"open={view.IsOpen} panel=0x{(long)view.Panel:X}");
        context.Check(view.Accept?.Rect is { Width: > 0, Height: > 0 }, "accept control",
            FormatControl(view.Accept));
        context.Check(view.Cancel?.Rect is { Width: > 0, Height: > 0 }, "cancel control",
            FormatControl(view.Cancel));
        context.Observe("vendor proceeds", FormatItems(view.VendorOffer));
        context.Observe("player offer", FormatItems(view.PlayerOffer));
        context.Check(view.VendorOffer.Count == 0 && view.PlayerOffer.Count == 0,
            "empty prepared offer",
            $"vendorItems={view.VendorOffer.Count} playerItems={view.PlayerOffer.Count}");
        return Task.FromResult(view.IsOpen && view.Accept?.Rect is not null && view.Cancel?.Rect is not null
            && view.VendorOffer.Count == 0 && view.PlayerOffer.Count == 0
            ? LiveTestCaseResult.Pass("empty sell window and both semantic controls are readable", "ReadOnlyFingerprint")
            : LiveTestCaseResult.Blocked("prepared sell window is unreadable or non-empty", "PreparedStateMismatch"));
    }

    private static string FormatControl(SellWindowView.Control? control)
        => control is null ? "missing" : $"text='{control.Text}' element=0x{(long)control.Element:X} rect={control.Rect}";

    private static string FormatItems(IEnumerable<SellWindowView.Item> items)
    {
        var values = items.Select(x =>
            $"{x.BaseName}[{x.Metadata}] stack={x.StackSize} size={x.Width}x{x.Height} rect={x.Rect}").ToArray();
        return values.Length == 0 ? "empty" : string.Join(" | ", values);
    }
}
