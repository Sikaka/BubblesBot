using System.Globalization;
using BubblesBot.Research.Validation;

namespace BubblesBot.Research.Probing.Oracle;

/// <summary>
/// <see cref="IGameOracle"/> backed by POEMCP (ExileAPI). Wraps the existing async
/// <see cref="PoemcpClient"/> behind a synchronous facade — probes are simple straight-line code
/// and this is dev-time tooling, so blocking on the eval round-trip is fine.
///
/// <para>Construct via <see cref="TryConnect"/>, which pings first and returns null if ExileAPI
/// isn't answering, so callers can cleanly fall back to <see cref="NullOracle"/>.</para>
/// </summary>
public sealed class PoemcpOracle : IGameOracle, IDisposable
{
    private readonly PoemcpClient _client;

    private PoemcpOracle(PoemcpClient client) => _client = client;

    public bool IsAvailable { get; private set; }

    /// <summary>Ping POEMCP; return a live oracle on success, null otherwise.</summary>
    public static PoemcpOracle? TryConnect(string baseUrl = "http://localhost:5999")
    {
        var client = new PoemcpClient(baseUrl);
        var alive = client.PingAsync().GetAwaiter().GetResult();
        if (!alive) { client.Dispose(); return null; }
        return new PoemcpOracle(client) { IsAvailable = true };
    }

    public bool TryGetValue(string key, out string value)
    {
        value = "";
        return OracleKeys.Values.TryGetValue(key, out var expr) && TryEval(expr, out value);
    }

    public bool TryGetAddress(string key, out nint addr)
    {
        addr = 0;
        if (!OracleKeys.Addresses.TryGetValue(key, out var expr)) return false;
        if (!TryEval(expr, out var hex) || hex.Length == 0) return false;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        addr = (nint)v;
        return true;
    }

    public bool TryEval(string expression, out string result)
    {
        result = "";
        if (!IsAvailable) return false;
        try
        {
            var r = _client.EvalAsync(expression).GetAwaiter().GetResult();
            if (!r.Success) return false;
            result = r.AsString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
