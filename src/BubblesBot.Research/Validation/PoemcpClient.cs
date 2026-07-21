using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace BubblesBot.Research.Validation;

/// <summary>
/// Talks to POEMCP â€” a companion ExileCore plugin exposing live game state on localhost:5999.
/// We use it for development-time validation: POEMCP gives us ground truth, our reader walks the
/// same memory directly, and every match is one validated offset chain.
///
/// /eval is the universal endpoint â€” accepts a C# expression evaluated in a GameController-bound
/// scripting context. We avoid /state and /state/player because POEMCP's serializer chokes on
/// NaN/Infinity values that some game fields legitimately hold.
/// </summary>
public sealed class PoemcpClient : IDisposable
{
    private readonly HttpClient _http;
    public Uri BaseAddress { get; }

    public PoemcpClient(string baseUrl = "http://localhost:5999")
    {
        BaseAddress = new Uri(baseUrl);
        // 60s timeout: POEMCP runs eval on the game thread, which can stall for several seconds
        // during area transitions, loading screens, etc. Don't fail prematurely.
        _http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(60) };
    }

    public int Retries { get; set; } = 2;
    /// <summary>When true, EvalAsync returns the first failure immediately â€” used after the initial PingAsync fails.</summary>
    public bool DegradedMode { get; set; }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var saved = Retries;
            Retries = 0;
            var r = await EvalAsync("\"ok\"", ct);
            Retries = saved;
            return r.Success && r.AsString() == "ok";
        }
        catch { return false; }
    }

    /// <summary>
    /// Evaluate a C# expression in POEMCP's scripting context. Retries on transient timeouts â€”
    /// POEMCP returns success:false with a 15s game-thread-timeout error when the game's main thread is busy.
    /// Retry count is configurable via <see cref="Retries"/> (default 2).
    /// </summary>
    public async Task<EvalResult> EvalAsync(string code, CancellationToken ct = default)
    {
        if (DegradedMode)
            return new EvalResult(false, default, "", "POEMCP marked unavailable (DegradedMode set)");

        Exception? last = null;
        var retries = Retries;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                var result = await EvalOnceAsync(code, ct);
                if (result.Success) return result;

                // Game-thread timeout from POEMCP â€” wait and retry.
                if (result.Error.Contains("game thread", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("timed out",  StringComparison.OrdinalIgnoreCase))
                {
                    if (attempt < retries) await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 2), ct);
                    continue;
                }

                // Non-transient error â€” return as-is.
                return result;
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
            {
                last = ex;
                if (attempt < retries) await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 2), ct);
            }
        }

        return new EvalResult(false, default, "", last?.Message ?? "POEMCP unreachable after retries");
    }

    private async Task<EvalResult> EvalOnceAsync(string code, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/eval", new { code }, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var success = root.TryGetProperty("success", out var sEl) && sEl.GetBoolean();
        if (!success)
        {
            var err = root.TryGetProperty("error", out var eEl) ? eEl.GetString() ?? "" : "";
            return new EvalResult(false, default, "", err);
        }

        var resultElement = root.TryGetProperty("result", out var r) ? r.Clone() : default;
        var resultType = root.TryGetProperty("resultType", out var t) ? t.GetString() ?? "" : "";
        return new EvalResult(true, resultElement, resultType, "");
    }

    public void Dispose() => _http.Dispose();
}

public readonly record struct EvalResult(bool Success, JsonElement Result, string ResultType, string Error)
{
    public string AsString()
    {
        return Result.ValueKind switch
        {
            JsonValueKind.String => Result.GetString() ?? "",
            JsonValueKind.Null   => "",
            _                    => Result.ToString(),
        };
    }

    public bool AsBool() => Result.ValueKind == JsonValueKind.True
        || (Result.ValueKind == JsonValueKind.String && bool.TryParse(Result.GetString(), out var b) && b);

    public int AsInt() => Result.ValueKind switch
    {
        JsonValueKind.Number => Result.GetInt32(),
        JsonValueKind.String => int.Parse(Result.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Cannot read {Result.ValueKind} as int"),
    };

    public long AsLong() => Result.ValueKind switch
    {
        JsonValueKind.Number => Result.GetInt64(),
        JsonValueKind.String => long.Parse(Result.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Cannot read {Result.ValueKind} as long"),
    };

    public float AsFloat() => Result.ValueKind switch
    {
        JsonValueKind.Number => Result.GetSingle(),
        JsonValueKind.String => float.Parse(Result.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Cannot read {Result.ValueKind} as float"),
    };

    /// <summary>
    /// Read a result that's a hex address string (no "0x" prefix; POEMCP's `.ToString("X")` convention).
    /// Falls back to parsing decimal addresses returned as numbers.
    /// </summary>
    public nint AsAddress()
    {
        if (Result.ValueKind == JsonValueKind.Number)
            return (nint)Result.GetInt64();
        if (Result.ValueKind == JsonValueKind.Null)
            return 0;
        var s = Result.GetString() ?? throw new InvalidOperationException("Expected string for AsAddress");
        if (s is "null" or "Null" or "NULL" || s.StartsWith("null", StringComparison.Ordinal))
            return 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new InvalidOperationException($"Could not parse '{s}' as address");
        return unchecked((nint)v);
    }
}
