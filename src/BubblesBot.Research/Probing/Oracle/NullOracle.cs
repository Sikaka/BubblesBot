namespace BubblesBot.Research.Probing.Oracle;

/// <summary>
/// Stand-in oracle used when ExileAPI/POEMCP is absent. Always unavailable; every lookup fails,
/// so probes fall through to their independent (value-scan / baseline) paths. Lets probe code
/// call <c>ctx.Oracle.TryGetValue(...)</c> unconditionally without null checks.
/// </summary>
public sealed class NullOracle : IGameOracle
{
    public static readonly NullOracle Instance = new();

    public bool IsAvailable => false;
    public bool TryGetValue(string key, out string value) { value = ""; return false; }
    public bool TryGetAddress(string key, out nint addr) { addr = 0; return false; }
    public bool TryEval(string expression, out string result) { result = ""; return false; }
}
