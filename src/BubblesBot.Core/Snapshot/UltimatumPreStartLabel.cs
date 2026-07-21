using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// One Ultimatum modifier choice. <see cref="Id"/> is the stable PoE id used by the danger
/// table (e.g. <c>RevenantDaemon2</c>). <see cref="Name"/> is the localized display string
/// (<c>"Stalking Ruin II"</c>) — used for diagnostics only.
/// </summary>
public readonly record struct UltimatumModifierChoice(string Id, string Name, string Description);

/// <summary>
/// Pre-start ground label for an <c>UltimatumChallengeInteractable</c>. Before the player
/// clicks BEGIN, the spawner shows a large floating panel via PoE's ItemsOnGround label
/// system — encounter type + first-round mod choices + initial reward + a BEGIN button. This
/// view exposes the rect math for clicking + the encounter-type text.
///
/// <para><b>Tree shape (validated 2026-05-18 against Mesa 83 Survive Ultimatum):</b>
/// <list type="bullet">
///   <item><c>root.Children[1].Children[1].Text</c> — encounter-type description
///         (e.g. <c>"Survive\r\nMonsters Enrage after a time"</c>,
///         <c>"Stand in the Stone Circles\r\n..."</c>). Substring-match <c>"Circle"</c> to
///         detect the Trial-of-Glory variant.</item>
///   <item><c>root.Children[2].Children[0].Children[0..N]</c> — N mod-choice icon buttons
///         (3 for most encounters). Each is a 53×53 leaf with no readable text — we click by
///         rect-center. The actual mod ids live in a typed memory wrapper we don't read yet,
///         so v1 picks by user-configured slot index rather than danger score.</item>
///   <item><c>root.Children[6]</c> — BEGIN button (~32×34 rect, right side). Click center to
///         start the encounter once a mod is chosen.</item>
/// </list></para>
///
/// <para><b>Discovery.</b> Caller passes the <see cref="GroundLabelView"/> for the spawner
/// (found via path match in <see cref="EntityCache"/>). This view does NOT re-scan the label
/// list — it expects the caller to have already located the spawner's label.</para>
/// </summary>
public sealed class UltimatumPreStartLabel
{
    private readonly MemoryReader _reader;
    private readonly nint _rootAddress;

    public bool IsValid => _rootAddress != 0;

    private UltimatumPreStartLabel(MemoryReader reader, nint rootAddress)
    {
        _reader = reader;
        _rootAddress = rootAddress;
    }

    /// <summary>
    /// Walk from the ground-label element to the panel root. The label's outermost element
    /// is decorative; <c>Children[0].Children[0]</c> is where the 7-child encounter panel
    /// sits. Returns an invalid view (<see cref="IsValid"/>=false) when the chase fails.
    /// </summary>
    public static UltimatumPreStartLabel FromGroundLabel(MemoryReader reader, nint labelElementAddress)
    {
        if (labelElementAddress == 0) return new UltimatumPreStartLabel(reader, 0);
        var c1 = ChildAddress(reader, labelElementAddress, 0);
        if (c1 == 0) return new UltimatumPreStartLabel(reader, 0);
        var root = ChildAddress(reader, c1, 0);
        return new UltimatumPreStartLabel(reader, root);
    }

    /// <summary>
    /// Encounter-type description. Reads <c>root[1][1].Text</c>. Returns empty when the
    /// element isn't there yet (panel still rendering).
    /// </summary>
    public string EncounterType
    {
        get
        {
            if (_rootAddress == 0) return string.Empty;
            var c1 = ChildAddress(_reader, _rootAddress, 1);
            if (c1 == 0) return string.Empty;
            var c11 = ChildAddress(_reader, c1, 1);
            if (c11 == 0) return string.Empty;
            return ElementText(_reader, c11);
        }
    }

    /// <summary>True when the encounter type is the Trial-of-Glory variant (stand-in-circle).</summary>
    public bool IsStandInCircleVariant
        => EncounterType.Contains("Circle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Number of mod-choice buttons offered for round 1. Typically 3. Returns 0 when the
    /// panel hasn't populated yet.
    /// </summary>
    public int ModChoiceCount => ModChoicesContainer is { } c ? ReadChildCount(_reader, c) : 0;

    /// <summary>Rect of the i-th mod-choice icon. Click its center to pick that mod.</summary>
    public ElementGeometry.Rect? ModChoiceRect(int index)
    {
        var container = ModChoicesContainer;
        if (container is null) return null;
        var btn = ChildAddress(_reader, container.Value, index);
        if (btn == 0) return null;
        return ElementGeometry.TryReadRect(_reader, btn);
    }

    /// <summary>
    /// The 3 (or however many) modifier choices offered for the next round. IDs are read from
    /// PoE's UltimatumModifiers.dat via the choice-panel's StdVector at +0x310. The list is
    /// ordered the same as <see cref="ModChoiceRect"/> — index N here picks the N-th rect.
    /// Empty list when the panel isn't rendering modifiers yet.
    /// </summary>
    public IReadOnlyList<UltimatumModifierChoice> Modifiers
    {
        get
        {
            var panel = ChoicePanelAddress;
            if (panel == 0) return Array.Empty<UltimatumModifierChoice>();
            return ReadModifiers(_reader, panel);
        }
    }

    /// <summary>The <c>UltimatumChoicePanel</c> element memory address (root.Children[2]).</summary>
    public nint ChoicePanelAddress
    {
        get
        {
            if (_rootAddress == 0) return 0;
            return ChildAddress(_reader, _rootAddress, 2);
        }
    }

    /// <summary>
    /// Currently-selected modifier index, or <c>-1</c> when nothing's selected. Reads the
    /// <see cref="KnownOffsets.UltimatumChoicePanel.SelectedChoice"/> int field on the panel.
    /// Use this to verify a mod-click registered before clicking BEGIN.
    /// </summary>
    public int SelectedChoice
    {
        get
        {
            var panel = ChoicePanelAddress;
            if (panel == 0) return -1;
            if (!_reader.TryReadStruct<int>(panel + KnownOffsets.UltimatumChoicePanel.SelectedChoice, out var v))
                return -1;
            return v;
        }
    }

    public static IReadOnlyList<UltimatumModifierChoice> ReadModifiers(MemoryReader reader, nint choicePanelAddress)
    {
        if (!reader.TryReadStruct<nint>(choicePanelAddress + KnownOffsets.UltimatumChoicePanel.ModifiersBegin, out var begin) || begin == 0)
            return Array.Empty<UltimatumModifierChoice>();
        if (!reader.TryReadStruct<nint>(choicePanelAddress + KnownOffsets.UltimatumChoicePanel.ModifiersBegin + 8, out var end))
            return Array.Empty<UltimatumModifierChoice>();
        var byteLen = (long)end - (long)begin;
        if (byteLen <= 0 || byteLen > 4096) return Array.Empty<UltimatumModifierChoice>();
        var count = (int)(byteLen / KnownOffsets.UltimatumChoicePanel.ModifierRecordSize);
        if (count <= 0 || count > 16) return Array.Empty<UltimatumModifierChoice>();

        var list = new List<UltimatumModifierChoice>(count);
        for (var i = 0; i < count; i++)
        {
            var recordAddr = begin + i * KnownOffsets.UltimatumChoicePanel.ModifierRecordSize;
            if (!reader.TryReadStruct<nint>(recordAddr, out var modAddr) || modAddr == 0) continue;

            var id   = ReadUtf16AtPtr(reader, modAddr + KnownOffsets.UltimatumModifier.IdPtr);
            var name = ReadUtf16AtPtr(reader, modAddr + KnownOffsets.UltimatumModifier.NamePtr);
            var desc = ReadUtf16AtPtr(reader, modAddr + KnownOffsets.UltimatumModifier.DescriptionPtr);
            list.Add(new UltimatumModifierChoice(id, name, desc));
        }
        return list;
    }

    /// <summary>
    /// Read a pointer at <paramref name="address"/>, then read a null-terminated UTF-16
    /// string from the pointed location. PoE's modifier strings live in the data-file heap
    /// as raw wide strings (not NativeString — different shape).
    /// </summary>
    private static string ReadUtf16AtPtr(MemoryReader reader, nint address)
    {
        if (!reader.TryReadStruct<nint>(address, out var heap) || heap == 0) return string.Empty;
        // Read up to 512 UTF-16 chars (1024 bytes) and trim at the first NUL.
        const int maxChars = 512;
        try
        {
            var s = reader.ReadStringUtf16(heap, maxChars);
            var nul = s.IndexOf('\0');
            return nul >= 0 ? s[..nul] : s;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// BEGIN button rect. Path validated 2026-05-18 via cursor-hover probe (UIHoverElement
    /// while user pointed at BEGIN matched <c>root.Children[4].Children[0]</c> exactly,
    /// addr 0x3B437444F10, texture <c>Common/3.dds</c>).
    ///
    /// <para><b>Note on the earlier probe.</b> Pre-mod-selection inspection saw a button-
    /// shaped element at <c>root.Children[6]</c> with the visible text "Begin", which led me
    /// to commit that path. Post-mod-selection that child becomes <c>IsVisible=false</c> and
    /// the real BEGIN appears as <c>root.Children[4].Children[0]</c>. Our state machine
    /// only clicks BEGIN <em>after</em> the mod is verified-selected, so this is the right
    /// path for that point in the flow.</para>
    /// </summary>
    public ElementGeometry.Rect? BeginButtonRect
    {
        get
        {
            if (_rootAddress == 0) return null;
            var c4 = ChildAddress(_reader, _rootAddress, 4);
            if (c4 == 0) return null;
            var begin = ChildAddress(_reader, c4, 0);
            if (begin == 0) return null;
            return ElementGeometry.TryReadRect(_reader, begin);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private nint? ModChoicesContainer
    {
        get
        {
            if (_rootAddress == 0) return null;
            var c2 = ChildAddress(_reader, _rootAddress, 2);
            if (c2 == 0) return null;
            var inner = ChildAddress(_reader, c2, 0);
            return inner == 0 ? null : inner;
        }
    }

    private static nint ChildAddress(MemoryReader reader, nint parent, int index)
    {
        if (!reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs, out var begin) || begin == 0) return 0;
        if (!reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs + 8, out var end)) return 0;
        var count = (int)(((long)end - (long)begin) / 8);
        if (index < 0 || index >= count) return 0;
        if (!reader.TryReadStruct<nint>(begin + index * 8, out var ptr)) return 0;
        return ptr;
    }

    private static int ReadChildCount(MemoryReader reader, nint element)
    {
        if (!reader.TryReadStruct<nint>(element + KnownOffsets.Element.Childs, out var begin)) return 0;
        if (!reader.TryReadStruct<nint>(element + KnownOffsets.Element.Childs + 8, out var end)) return 0;
        return (int)(((long)end - (long)begin) / 8);
    }

    /// <summary>
    /// Read an Element's display text. PoE stores it as a NativeString at a (validated)
    /// Element offset; the same channel <see cref="ElementGeometry"/> would expose if it
    /// surfaced text. Falls back to empty on read failure.
    /// </summary>
    private static string ElementText(MemoryReader reader, nint element)
        => NativeString.Read(reader, element + KnownOffsets.Element.Text);
}
