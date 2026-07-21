using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Probing.Toolkit;

/// <summary>
/// The resolved root pointer chain for one run: IngameState + IngameData, plus lazy accessors for
/// the common sub-objects probes anchor on. Resolved once (see <see cref="ChainResolver"/>) and
/// shared via <see cref="ProbeContext"/>. All reads are defensive: a torn-down pointer yields 0,
/// never a throw.
/// </summary>
public sealed class ResolvedChain
{
    private readonly MemoryReader _r;
    private Dictionary<string, nint>? _playerComponents;

    public nint IngameState { get; }
    public nint IngameData { get; }

    /// <summary>How the chain was found, for logging ("AOB" or "value-scan").</summary>
    public string ResolvedVia { get; }

    public ResolvedChain(MemoryReader reader, nint ingameState, nint ingameData, string resolvedVia)
    {
        _r = reader;
        IngameState = ingameState;
        IngameData = ingameData;
        ResolvedVia = resolvedVia;
    }

    public bool IsValid => IngameData != 0 && IngameState != 0;

    public nint Player => Ptr(IngameData + KnownOffsets.IngameData.LocalPlayer);
    public nint Camera => Ptr(IngameState + KnownOffsets.IngameState.Camera);
    public nint IngameUi => Ptr(IngameState + KnownOffsets.IngameState.IngameUi);
    public nint UiRoot => Ptr(IngameState + KnownOffsets.IngameState.UIRoot);
    public nint ServerData => Ptr(IngameData + KnownOffsets.IngameData.ServerData);
    public nint EntityList => Ptr(IngameData + KnownOffsets.IngameData.EntityList);

    /// <summary>Player component instance address by name (e.g. "Life"), or 0. Cached per run.</summary>
    public nint PlayerComponent(string name)
    {
        _playerComponents ??= EntityComponents.ReadComponentMap(_r, Player);
        return _playerComponents.TryGetValue(name, out var a) ? a : 0;
    }

    private nint Ptr(nint at) => _r.TryReadStruct<nint>(at, out var p) ? p : 0;
}
