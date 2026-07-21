using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Lazy view onto the local player. All component reads are cached for the lifetime of the
/// owning <see cref="GameSnapshot"/> (one tick). Properties that aren't touched cost nothing.
/// </summary>
public sealed class PlayerView
{
    private readonly MemoryReader _reader;
    private readonly nint _playerAddress;

    private IReadOnlyDictionary<string, nint>? _components;
    private bool _gridRead; private Vector2i _grid;
    private bool _lifeRead; private VitalStruct _life;
    private BuffsView? _buffs;

    internal PlayerView(MemoryReader reader, nint playerAddress)
    {
        _reader = reader;
        _playerAddress = playerAddress;
    }

    public nint Address => _playerAddress;

    public Vector2i GridPosition
    {
        get
        {
            if (_gridRead) return _grid;
            _gridRead = true;
            var positioned = Component("Positioned");
            if (positioned != 0)
                _reader.TryReadStruct(positioned + KnownOffsets.PositionedComponent.GridPosition, out _grid);
            return _grid;
        }
    }

    public VitalStruct Life
    {
        get
        {
            if (_lifeRead) return _life;
            _lifeRead = true;
            var life = Component("Life");
            if (life != 0)
                _reader.TryReadStruct(life + KnownOffsets.LifeComponent.Health, out _life);
            return _life;
        }
    }

    /// <summary>Active buffs/debuffs on the player. Lazy: read on first touch.</summary>
    public BuffsView Buffs => _buffs ??= new BuffsView(_reader, Component("Buffs"));

    /// <summary>Live address of the Actor component, for callers that read cooldown/skill state.</summary>
    public nint ActorComponentAddress => Component("Actor");

    /// <summary>
    /// Character name from the Player component. Used to key per-character config profiles.
    /// Returns empty string when unreadable (loading screen, no player). Cached for the
    /// snapshot's lifetime.
    /// </summary>
    public string CharacterName
    {
        get
        {
            if (_nameRead) return _name;
            _nameRead = true;
            var pc = Component("Player");
            if (pc == 0) return _name = string.Empty;
            return _name = ReadNativeString(_reader, pc + KnownOffsets.PlayerComponent.PlayerName);
        }
    }

    private bool _nameRead;
    private string _name = string.Empty;

    /// <summary>
    /// Read a PoE NativeString (SSO union: ≤7 UTF-16 chars inline at +0x00, otherwise pointer
    /// at +0x00). Length lives at +0x10. Returns empty string on any failure.
    /// </summary>
    private static string ReadNativeString(MemoryReader reader, nint addr)
    {
        if (!reader.TryReadStruct<int>(addr + 0x10, out var length) || length <= 0 || length > 256)
            return string.Empty;
        try
        {
            if (length < 8)
                return reader.ReadStringUtf16(addr, length);
            if (!reader.TryReadStruct<nint>(addr, out var ptr) || ptr == 0) return string.Empty;
            return reader.ReadStringUtf16(ptr, length);
        }
        catch { return string.Empty; }
    }

    /// <summary>Compute grid distance from the player to an arbitrary grid position.</summary>
    public float DistanceTo(Vector2i otherGrid)
    {
        var g = GridPosition;
        var dx = (float)(otherGrid.X - g.X);
        var dy = (float)(otherGrid.Y - g.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private nint Component(string name)
    {
        _components ??= EntityComponents.ReadComponentMap(_reader, _playerAddress);
        return _components.TryGetValue(name, out var addr) ? addr : 0;
    }
}
