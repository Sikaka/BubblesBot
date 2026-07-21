using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Read-side cursor action state. A held item's durable identity must be joined with the exact
/// container delta that caused <see cref="CursorAction.HoldItem"/>; the cursor action alone does
/// not identify which item is held.
/// </summary>
public sealed class CursorView
{
    public enum CursorAction : byte
    {
        Free = 0,
        HoldItem = 1,
        UseItem = 2,
        HoldItemForSell = 3,
        Unknown = byte.MaxValue,
    }

    public bool IsReadable { get; }
    public nint Address { get; }
    public CursorAction Action { get; }

    private CursorView(bool isReadable, nint address, CursorAction action)
    {
        IsReadable = isReadable;
        Address = address;
        Action = action;
    }

    public static CursorView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        nint cursor = 0;
        if (!reader.TryReadStruct<nint>(
                ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0
            || !reader.TryReadStruct<nint>(
                ingameUi + KnownOffsets.IngameUiElements.Mouse, out cursor)
            || cursor == 0
            || !reader.TryReadStruct<byte>(cursor + KnownOffsets.Cursor.Action, out var raw))
            return new CursorView(false, cursor, CursorAction.Unknown);

        var action = raw <= (byte)CursorAction.HoldItemForSell
            ? (CursorAction)raw
            : CursorAction.Unknown;
        return new CursorView(action != CursorAction.Unknown, cursor, action);
    }
}
