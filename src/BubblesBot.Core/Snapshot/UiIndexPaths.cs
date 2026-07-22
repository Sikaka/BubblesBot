namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Committed UI tree index paths from <see cref="Game.KnownOffsets.IngameState.UIRoot"/>.
/// These ARE the bot's "offsets" for panels — runtime navigation walks
/// <c>UIRoot.Children[a].Children[b]...</c> using the integers below. Per-patch maintenance
/// runs <c>--discover-ui-paths</c> to recover updated paths and commits the diff here.
///
/// <para>Each constant pairs with a <see cref="UiPattern"/> in <see cref="UiPatterns"/>.
/// Discovery never silently rewrites these — the human reviews + commits, the same way
/// <see cref="Game.KnownOffsets"/> changes flow through PRs.</para>
///
/// <para><b>Empty path = not yet discovered.</b> Run discovery with the panel open, paste
/// the proposed path here.</para>
/// </summary>
public static class UiIndexPaths
{
    /// <summary>
    /// MapDeviceWindow path from UIRoot. Discovered 2026-05-07 against the
    /// Test character with the device open. Confidence 1.00 — every
    /// child constraint in <c>UiPatterns.MapDeviceWindow</c> matched exactly; the next
    /// closest candidate scored 0.55, so this is unambiguous. Re-run discovery on each
    /// patch to confirm.
    /// </summary>
    public static readonly UiIndexPath MapDeviceWindow = new(1, 29, 7, 0);

    /// <summary>
    /// Global Ritual Favours button shown on the right-side league HUD. Unlike the altar
    /// world entity, this can open the reward shop from anywhere in the map. Discovered
    /// 2026-07-15 by walking the hovered 4/4 button back to UIRoot.
    /// </summary>
    public static readonly UiIndexPath RitualRewardsButton = new(1, 142, 7, 19, 0);
}
