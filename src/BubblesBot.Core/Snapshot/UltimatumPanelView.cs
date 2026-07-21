using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Wraps the in-encounter <c>UltimatumPanel</c> — the between-wave choice / accumulated-
/// reward / next-reward / mod-row / take-reward / accept-trial panel that appears after
/// each successful wave. Built once per snapshot; consumers call <see cref="IsVisible"/>
/// to gate before reading other fields.
///
/// <para><b>Two panels, two surfaces.</b>
/// <list type="bullet">
///   <item><b>Pre-start ground label</b> (player hasn't started yet) — encounter-type text
///         + first-round mod choices + BEGIN button live under the spawner's hover label.
///         Read via <see cref="UltimatumPreStartLabel"/>.</item>
///   <item><b>In-encounter panel</b> (THIS class) — shown after each wave. Larger 2-column
///         layout with Current Reward + Next Reward + mod choices + accept/take buttons.</item>
/// </list></para>
///
/// <para><b>Tree paths validated 2026-05-18 against a Mesa(83) Survive round 2:</b>
/// <list type="bullet">
///   <item><c>panel.Children[0].Children[3].Text</c> — "Round &lt;ultimatumnumber&gt;{N/M}".</item>
///   <item><c>panel.Children[1].Children[1].Children[0].Children[1]</c> — current reward
///         NormalInventoryItem (Item entity at <see cref="KnownOffsets.NormalInventoryItem.ItemPtr"/>).</item>
///   <item><c>panel.Children[1].Children[4].Children[0]</c> — Take Reward button rect (169×41).</item>
///   <item><c>panel.Children[2].Children[1].Children[1].Children[1]</c> — next reward NormalInventoryItem.</item>
///   <item><c>panel.Children[2].Children[4].Children[0].Children[0..2]</c> — 3 mod-choice
///         icons (52×53 each), same row as <see cref="UltimatumPreStartLabel"/>.</item>
///   <item><c>panel.Children[2].Children[6].Children[0]</c> — Accept Trial (continue) button (169×41).</item>
/// </list></para>
///
/// <para><b>ChoicesPanel sub-element</b> at <c>panel + 0x</c><see cref="KnownOffsets.UltimatumPanel.ChoicesPanelPtr"/>
/// — same memory shape as the pre-start panel (StdVector of modifier records at +0x310,
/// SelectedChoice int at +0x328). Mod IDs read through this; click rects go via the tree paths.</para>
/// </summary>
public sealed class UltimatumPanelView
{
    private readonly MemoryReader _reader;
    public nint PanelAddress { get; }
    public bool IsVisible { get; }

    public UltimatumPanelView(MemoryReader reader, nint panelAddress)
    {
        _reader = reader;
        PanelAddress = panelAddress;
        IsVisible = panelAddress != 0 && IsElementVisible(reader, panelAddress);
    }

    /// <summary>
    /// Resolve via <c>IngameState → IngameUi → UltimatumPanel</c> pointer chase. Returns an
    /// instance with <see cref="IsVisible"/>=false when the panel isn't open.
    /// </summary>
    public static UltimatumPanelView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
            return new UltimatumPanelView(reader, 0);
        if (!reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.UltimatumPanel, out var panelAddr) || panelAddr == 0)
            return new UltimatumPanelView(reader, 0);
        return new UltimatumPanelView(reader, panelAddr);
    }

    // ── ChoicesPanel sub-element ─────────────────────────────────────────

    /// <summary>The nested <c>UltimatumChoicePanel</c> element address. Current builds expose
    /// it at <c>panel.Children[2].Children[4]</c>. Resolving through the element tree is more
    /// robust than the former native-field offset, which drifted while the visible panel
    /// layout and button paths remained stable.</summary>
    public nint ChoicesPanelAddress
    {
        get
        {
            if (PanelAddress == 0) return 0;
            return DescendantAddress(2, 4);
        }
    }

    /// <summary>The 3 modifier choices offered for the next wave. Empty when the panel
    /// isn't fully rendered yet or there's no ChoicesPanel.</summary>
    public IReadOnlyList<UltimatumModifierChoice> Modifiers
    {
        get
        {
            var cp = ChoicesPanelAddress;
            if (cp == 0) return Array.Empty<UltimatumModifierChoice>();
            return UltimatumPreStartLabel.ReadModifiers(_reader, cp);
        }
    }

    /// <summary>Currently-selected modifier index, or -1 when nothing's selected. Used as the
    /// verify signal after clicking a mod icon.</summary>
    public int SelectedChoice
    {
        get
        {
            var cp = ChoicesPanelAddress;
            if (cp == 0) return -1;
            if (!_reader.TryReadStruct<int>(cp + KnownOffsets.UltimatumChoicePanel.SelectedChoice, out var v))
                return -1;
            return v;
        }
    }

    // ── Round counter ───────────────────────────────────────────────────

    /// <summary>
    /// Raw round-counter text — "Round &lt;ultimatumnumber&gt;{2/10}" or similar. Empty when
    /// not readable. Use <see cref="RoundProgress"/> for the parsed (current, max) pair.
    /// </summary>
    public string RoundCounterText
    {
        get
        {
            if (PanelAddress == 0) return string.Empty;
            var el = DescendantAddress(0, 3);
            if (el == 0) return string.Empty;
            return NativeString.Read(_reader, el + KnownOffsets.Element.Text);
        }
    }

    /// <summary>
    /// Parsed (current, max) panel numbers, or null if the text doesn't match the expected
    /// format. PoE renders "Round &lt;tag&gt;{N/M}" — we ignore the tag and pull both ints
    /// out of the curly-brace group. On the visible between-wave panel, Current identifies
    /// the wave offered by Accept Trial (2/10 after wave 1 completes). Never interpret a
    /// hidden post-accept value as either a completed wave or terminal evidence.
    /// </summary>
    public (int Current, int Max)? RoundProgress
    {
        get
        {
            var text = RoundCounterText;
            if (string.IsNullOrEmpty(text)) return null;
            var lbrace = text.IndexOf('{');
            var rbrace = text.IndexOf('}');
            if (lbrace < 0 || rbrace < 0 || rbrace <= lbrace) return null;
            var inner = text[(lbrace + 1)..rbrace];
            var slash = inner.IndexOf('/');
            if (slash < 0) return null;
            if (!int.TryParse(inner[..slash], out var cur)) return null;
            if (!int.TryParse(inner[(slash + 1)..], out var max)) return null;
            return (cur, max);
        }
    }

    // ── Reward items ────────────────────────────────────────────────────

    /// <summary>Address of the Entity wrapped by the current-reward inventory slot, or 0 if
    /// missing. Pass to <c>EntityListReader.ReadEntityPath</c> for the
    /// <c>Metadata/Items/...</c> path; look up chaos value via the price catalog.</summary>
    public nint CurrentRewardItemAddress => ReadInventoryItemEntity(1, 1, 0, 1);

    /// <summary>Address of the Entity wrapped by the next-reward inventory slot.</summary>
    public nint NextRewardItemAddress => ReadInventoryItemEntity(2, 1, 1, 1);

    private nint ReadInventoryItemEntity(params int[] path)
    {
        var el = DescendantAddress(path);
        if (el == 0) return 0;
        if (!_reader.TryReadStruct<nint>(el + KnownOffsets.NormalInventoryItem.Item, out var item)) return 0;
        return item;
    }

    // ── Click targets ───────────────────────────────────────────────────

    /// <summary>The "take reward" button rect (claim accumulated rewards + end encounter).</summary>
    public ElementGeometry.Rect? TakeRewardButtonRect => RectAt(1, 4, 0);

    /// <summary>The "accept trial" button rect (commit chosen mod + start next wave).</summary>
    public ElementGeometry.Rect? AcceptTrialButtonRect => RectAt(2, 6, 0);

    /// <summary>Rect of the i-th mod-choice icon (0-based). 3 icons sit in a row at
    /// <c>panel.Children[2].Children[4].Children[0]</c>.</summary>
    public ElementGeometry.Rect? ModChoiceRect(int index)
    {
        if (index < 0 || index > 8) return null;
        return RectAt(2, 4, 0, index);
    }

    private ElementGeometry.Rect? RectAt(params int[] path)
    {
        var addr = DescendantAddress(path);
        if (addr == 0) return null;
        return ElementGeometry.TryReadRect(_reader, addr);
    }

    private nint DescendantAddress(params int[] path)
    {
        if (PanelAddress == 0) return 0;
        var addr = PanelAddress;
        foreach (var idx in path)
        {
            addr = ChildAddress(_reader, addr, idx);
            if (addr == 0) return 0;
        }
        return addr;
    }

    // ── Internal helpers ────────────────────────────────────────────────

    private static nint ChildAddress(MemoryReader reader, nint parent, int index)
    {
        if (!reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs, out var begin) || begin == 0) return 0;
        if (!reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs + 8, out var end)) return 0;
        var count = (int)(((long)end - (long)begin) / 8);
        if (index < 0 || index >= count) return 0;
        if (!reader.TryReadStruct<nint>(begin + index * 8, out var ptr)) return 0;
        return ptr;
    }

    private static bool IsElementVisible(MemoryReader reader, nint elementAddress)
        => BubblesBot.Core.Game.ElementReader.IsVisibleDeep(reader, elementAddress);
}
