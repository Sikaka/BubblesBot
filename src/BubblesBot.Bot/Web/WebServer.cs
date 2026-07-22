using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BubblesBot.Bot.Settings;

namespace BubblesBot.Bot.Web;

/// <summary>
/// Embedded HTTP + WebSocket server for the bot dashboard. Bound to localhost only — loopback
/// IS the auth boundary; every mutating request is additionally loopback-checked. LAN exposure
/// is a deliberate future feature that requires a real auth story first.
///
/// <para>Two transports serve the SAME routes via <see cref="RouteAsync"/>: the primary
/// <see cref="HttpListener"/> (HTTP.sys) and a raw <see cref="TcpListener"/> fallback used when
/// HTTP.sys refuses the URL reservation (a common non-admin condition on Windows). The fallback
/// serves the full API and SPA; only the live-status WebSocket is HttpListener-only, and the
/// SPA polls <c>/api/status</c> when the socket is unavailable.</para>
///
/// Routes:
///   GET   /api/settings        → { settings, version }
///   PUT   /api/settings        → replace (whitelist merge), validated; 422 on violations
///   PATCH /api/settings        → path-targeted ops with optional version CAS (409 on stale)
///   GET   /api/settings/schema → reflection-derived field metadata for UI rendering
///   GET   /api/status          → one-shot status snapshot (same fields the WS pushes)
///   GET   /api/meta            → environment/preflight snapshot (wizard + dashboard)
///   POST  /api/control/arm|disarm|mode → arm/disarm/switch-mode
///   GET|POST|PUT|DELETE /api/strategies[/...] → strategy library CRUD/activate/import/export
///   GET   /api/runs[?since=&before=&limit=] | /api/runs/{id} | /api/runs/summary → run history
///   POST  /api/incident        → manual incident marker into the event log
///   WS    /ws                  → live status stream at 10 Hz (HttpListener only)
///   GET   &lt;anything else&gt;      → SPA static assets with index.html client-route fallback
/// </summary>
public sealed class WebServer : IDisposable
{
    private const int    WebPort          = 5666;
    private const int    StatusHz         = 10;
    private const int    MaxBodyBytes     = 1_000_000;
    private const string AssetRoot        = "WebUI/wwwroot";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SettingsStore _settings;
    private readonly Func<object> _getStatus;
    private readonly IControlSurface _control;
    private readonly Strategies.StrategyStore _strategies;
    private readonly Diagnostics.RunReportStore _runReports;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WebSocket> _sockets = new();
    private readonly object _socketsLock = new();
    private Task? _acceptLoop;
    private Task? _broadcastLoop;
    private TcpListener? _fallbackListener;
    private Task? _fallbackLoop;
    private string _assetRoot = "";

    public WebServer(SettingsStore settings, Func<object> getStatus, IControlSurface control,
        Strategies.StrategyStore strategies, Diagnostics.RunReportStore runReports)
    {
        _settings   = settings;
        _getStatus  = getStatus;
        _control    = control;
        _strategies = strategies;
        _runReports = runReports;
        _listener   = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{WebPort}/");
    }

    public void Start()
    {
        _assetRoot = ResolveAssetRoot();
        // The raw-TCP fallback always runs: it guarantees the full app is reachable even when
        // HTTP.sys denies the reservation. When both bind, the fallback simply never wins the
        // port (HttpListener holds it) and its accept loop idles.
        try
        {
            _listener.Start();
            _acceptLoop    = Task.Run(AcceptLoopAsync);
            _broadcastLoop = Task.Run(BroadcastLoopAsync);
            Console.WriteLine($"Web UI: http://localhost:{WebPort}  (loopback only)");
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine(
                $"Web UI: HTTP.sys bind denied ({ex.Message}); serving full UI via loopback TCP fallback.");
            StartFallback();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _fallbackListener?.Stop(); } catch { }
        try { _acceptLoop?.Wait(500); } catch { }
        try { _broadcastLoop?.Wait(500); } catch { }
        try { _fallbackLoop?.Wait(500); } catch { }
        lock (_socketsLock)
        {
            foreach (var ws in _sockets)
            {
                try { ws.Abort(); } catch { }
                try { ws.Dispose(); } catch { }
            }
            _sockets.Clear();
        }
        try { _listener.Close(); } catch { }
    }

    // ── Transport-agnostic request/response ────────────────────────────────

    private sealed record WebRequest(string Method, string Path, string Query, string Body, bool IsLoopback);

    private sealed class WebResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = [];
        public List<(string Key, string Value)> Headers = [];

        public WebResponse WithHeader(string key, string value) { Headers.Add((key, value)); return this; }

        public static WebResponse Json(object payload, int status = 200) => new()
        {
            Status = status,
            Body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };
        public static WebResponse RawJson(string json, int status = 200) => new()
        {
            Status = status,
            Body = Encoding.UTF8.GetBytes(json),
        };
        public static WebResponse Bytes(int status, string contentType, byte[] body) => new()
        {
            Status = status, ContentType = contentType, Body = body,
        };
        public static WebResponse Text(int status, string body) => new()
        {
            Status = status, ContentType = "text/plain; charset=utf-8", Body = Encoding.UTF8.GetBytes(body),
        };
        public static WebResponse Empty(int status) => new() { Status = status, ContentType = "text/plain" };
    }

    /// <summary>The single routing table shared by both transports (WebSocket handled upstream).</summary>
    private async Task<WebResponse> RouteAsync(WebRequest req)
    {
        if (req.Path.StartsWith("/api/strategies", StringComparison.OrdinalIgnoreCase))
            return RouteStrategies(req);
        if (req.Path.StartsWith("/api/runs", StringComparison.OrdinalIgnoreCase))
            return RouteRuns(req);

        switch (req.Method, req.Path)
        {
            case ("GET", "/api/settings"):        return WebResponse.Json(SettingsEnvelope());
            case ("PUT", "/api/settings"):        return PutSettings(req);
            case ("PATCH", "/api/settings"):      return PatchSettings(req);
            case ("GET", "/api/settings/schema"): return WebResponse.Json(BuildSchema());
            case ("GET", "/api/status"):          return WebResponse.Json(_getStatus());
            case ("GET", "/api/meta"):            return WebResponse.Json(_control.Meta());
            case ("POST", "/api/control/arm"):    return ControlArm(req);
            case ("POST", "/api/control/disarm"): return Loopback(req) ?? Control(_control.Disarm());
            case ("POST", "/api/control/mode"):   return ControlMode(req);
            case ("POST", "/api/incident"):       return MarkIncident(req);
        }

        if (req.Method == "GET" && StaticFiles.Resolve(_assetRoot, req.Path) is { } asset)
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(asset.FilePath); }
            catch (IOException) { return WebResponse.Empty(404); }
            return WebResponse.Bytes(200, asset.ContentType, bytes).WithHeader("Cache-Control", asset.CacheControl);
        }
        return WebResponse.Empty(404);
    }

    /// <summary>403 response if the request isn't loopback; null to proceed.</summary>
    private static WebResponse? Loopback(WebRequest req) => req.IsLoopback ? null : WebResponse.Empty(403);

    private object SettingsEnvelope() => new { settings = _settings.Current, version = _settings.Version };

    private WebResponse PutSettings(WebRequest req)
    {
        if (Loopback(req) is { } denied) return denied;
        var next = TryDeserialize<BotSettings>(req.Body);
        if (next is null) return WebResponse.Text(400, "invalid settings");

        var candidate = Clone(_settings.Current);
        ApplySupportedSettings(candidate, next);
        var errors = SettingsValidator.Validate(candidate);
        if (errors.Count > 0) return WebResponse.Json(new { errors }, 422);

        _settings.Mutate(current => ApplySupportedSettings(current, next));
        return WebResponse.Json(SettingsEnvelope());
    }

    private sealed record PatchRequest(long? ExpectedVersion, List<SettingsPatcher.PatchOp>? Ops);

    private WebResponse PatchSettings(WebRequest req)
    {
        if (Loopback(req) is { } denied) return denied;
        var request = TryDeserialize<PatchRequest>(req.Body);
        if (request?.Ops is null || request.Ops.Count == 0) return WebResponse.Text(400, "no ops");
        if (request.ExpectedVersion is { } expected && expected != _settings.Version)
            return WebResponse.Json(SettingsEnvelope(), 409);

        var candidate = Clone(_settings.Current);
        var errors = SettingsPatcher.Apply(candidate, request.Ops, JsonOptions);
        if (errors.Count == 0) errors = SettingsValidator.Validate(candidate);
        if (errors.Count > 0) return WebResponse.Json(new { errors }, 422);

        _settings.Mutate(current => SettingsPatcher.Apply(current, request.Ops, JsonOptions));
        return WebResponse.Json(SettingsEnvelope());
    }

    private WebResponse ControlArm(WebRequest req)
    {
        if (Loopback(req) is { } denied) return denied;
        int? mode = null;
        if (req.Body.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(req.Body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("mode", out var modeProp)
                    && modeProp.ValueKind == JsonValueKind.Number)
                    mode = modeProp.GetInt32();
            }
            catch (JsonException) { return WebResponse.Text(400, "invalid body"); }
        }
        return Control(_control.Arm(mode));
    }

    private WebResponse ControlMode(WebRequest req)
    {
        if (Loopback(req) is { } denied) return denied;
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("mode", out var modeProp)
                || modeProp.ValueKind != JsonValueKind.Number)
                return WebResponse.Text(400, "mode required");
            var force = doc.RootElement.TryGetProperty("force", out var forceProp) && forceProp.ValueKind == JsonValueKind.True;
            return Control(_control.SwitchMode(modeProp.GetInt32(), force));
        }
        catch (JsonException) { return WebResponse.Text(400, "invalid body"); }
    }

    private static WebResponse Control(ControlResult result)
        => WebResponse.Json(new { result.Status, result.Warnings, result.Reasons }, result.Ok ? 200 : 409);

    private WebResponse MarkIncident(WebRequest req)
    {
        if (Loopback(req) is { } denied) return denied;
        var note = req.Body.Trim();
        if (note.Length > 500) note = note[..500];
        Diagnostics.EventLog.Emit("incident", "incident.manual", Diagnostics.EventSeverity.Warning,
            string.IsNullOrEmpty(note) ? "manual incident marker" : note);
        return WebResponse.Empty(204);
    }

    private static T? TryDeserialize<T>(string body)
    {
        try { return JsonSerializer.Deserialize<T>(body, JsonOptions); }
        catch (JsonException) { return default; }
    }

    private static BotSettings Clone(BotSettings settings)
        => JsonSerializer.Deserialize<BotSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    // ── Strategy library ───────────────────────────────────────────────────
    // Strategy documents serialize through StrategySerialization (polymorphic mechanic blocks,
    // string enums) so the wire format matches the on-disk / import / export format exactly.

    private WebResponse RouteStrategies(WebRequest req)
    {
        var segments = req.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // [0]="api", [1]="strategies", [2]=id|"templates"|"import", [3]="activate"|"export"

        if (segments.Length == 2)
        {
            return req.Method switch
            {
                "GET" => StrategyList(),
                "POST" => CreateStrategy(req),
                _ => WebResponse.Empty(405),
            };
        }

        var second = segments[2];
        if (segments.Length == 3 && second.Equals("templates", StringComparison.OrdinalIgnoreCase) && req.Method == "GET")
            return StrategyTemplateList();
        if (segments.Length == 3 && second.Equals("import", StringComparison.OrdinalIgnoreCase) && req.Method == "POST")
            return ImportStrategy(req);

        var id = Uri.UnescapeDataString(second);
        if (segments.Length == 3)
        {
            return req.Method switch
            {
                "GET" => GetStrategy(id),
                "PUT" => SaveStrategy(req, id),
                "DELETE" => DeleteStrategy(req, id),
                _ => WebResponse.Empty(405),
            };
        }
        if (segments.Length == 4 && req.Method == "POST" && segments[3].Equals("activate", StringComparison.OrdinalIgnoreCase))
            return ActivateStrategy(req, id);
        if (segments.Length == 4 && req.Method == "GET" && segments[3].Equals("export", StringComparison.OrdinalIgnoreCase))
            return ExportStrategy(id);
        return WebResponse.Empty(404);
    }

    private WebResponse StrategyList()
    {
        var activeId = _strategies.ActiveId;
        var summaries = _strategies.List().Select(s => new
        {
            id = s.Identity.Id,
            name = s.Identity.Name,
            description = s.Identity.Description,
            author = s.Identity.Author,
            gameVersion = s.Identity.GameVersion,
            updatedUtc = s.Identity.ModifiedUtc,
            mode = 4,
            active = string.Equals(s.Identity.Id, activeId, StringComparison.OrdinalIgnoreCase),
            valid = Strategies.StrategyValidator.Validate(s).Ok,
            summary = new
            {
                mapName = s.Supply.Map.TargetMapName,
                targetMaps = s.Completion.TargetMaps,
                mechanics = s.Mechanics.Where(m => m.Enabled).Select(m => m.MechanicId).ToArray(),
            },
        }).ToArray();
        return WebResponse.Json(new { strategies = summaries, activeId, loadErrors = _strategies.LoadErrors });
    }

    private static WebResponse StrategyTemplateList()
    {
        var templates = Strategies.StrategyTemplates.All().Select(t => new
        {
            templateId = t.TemplateId,
            name = t.Name,
            description = t.Description,
            mode = t.Mode,
        }).ToArray();
        return WebResponse.Json(new { templates });
    }

    private WebResponse GetStrategy(string id)
        => _strategies.TryGet(id, out var strategy)
            ? WebResponse.RawJson(Strategies.StrategySerialization.Serialize(strategy))
            : WebResponse.Empty(404);

    private sealed record CreateStrategyRequest(string? Name, string? FromTemplate, string? FromStrategy);

    private WebResponse CreateStrategy(WebRequest req)
    {
        if (Loopback(req) is { } denied) return denied;
        var request = TryDeserialize<CreateStrategyRequest>(req.Body);
        if (request is null) return WebResponse.Text(400, "invalid body");

        Strategies.FarmingStrategy doc;
        if (!string.IsNullOrEmpty(request.FromStrategy))
        {
            if (!_strategies.TryGet(request.FromStrategy, out doc))
                return WebResponse.Json(new { errors = new[] { $"unknown source strategy '{request.FromStrategy}'" } }, 422);
        }
        else
        {
            var template = Strategies.StrategyTemplates.Find(request.FromTemplate ?? Strategies.StrategyTemplates.CustomId);
            if (template is null)
                return WebResponse.Json(new { errors = new[] { $"unknown template '{request.FromTemplate}'" } }, 422);
            doc = Strategies.StrategySerialization.Clone(template.Doc);
        }

        doc.Identity.Id = Strategies.StrategyIdentity.NewId();
        doc.Identity.Name = string.IsNullOrWhiteSpace(request.Name)
            ? (string.IsNullOrWhiteSpace(doc.Identity.Name) ? "New strategy" : $"{doc.Identity.Name} (copy)")
            : request.Name.Trim();
        doc.Identity.CreatedUtc = default;
        _strategies.Save(doc);
        return WebResponse.RawJson(Strategies.StrategySerialization.Serialize(doc), 201);
    }

    private WebResponse SaveStrategy(WebRequest req, string id)
    {
        if (Loopback(req) is { } denied) return denied;
        Strategies.FarmingStrategy doc;
        try { doc = Strategies.StrategySerialization.Parse(req.Body); }
        catch (Strategies.StrategyFormatException ex) { return WebResponse.Json(new { errors = new[] { ex.Message } }, 422); }

        doc.Identity.Id = id;   // URL id is authoritative — a save cannot repoint another document
        var validation = _strategies.Save(doc);
        return WebResponse.Json(new
        {
            strategy = System.Text.Json.Nodes.JsonNode.Parse(Strategies.StrategySerialization.Serialize(doc)),
            errors = validation.Errors,
            warnings = validation.Warnings,
        });
    }

    private WebResponse DeleteStrategy(WebRequest req, string id)
    {
        if (Loopback(req) is { } denied) return denied;
        try { _strategies.Delete(id); return WebResponse.Empty(204); }
        catch (InvalidOperationException ex) { return WebResponse.Json(new { errors = new[] { ex.Message } }, 409); }
    }

    private WebResponse ActivateStrategy(WebRequest req, string id)
    {
        if (Loopback(req) is { } denied) return denied;
        var validation = _strategies.Activate(id);
        if (!validation.Ok) return WebResponse.Json(new { errors = validation.Errors, warnings = validation.Warnings }, 422);
        _settings.Mutate(s => s.ActiveStrategyId = id);
        return WebResponse.Json(new { activeId = id, warnings = validation.Warnings });
    }

    private WebResponse ImportStrategy(WebRequest req)
    {
        if (Loopback(req) is { } denied) return denied;
        var result = _strategies.Import(req.Body);
        return result.Strategy is null
            ? WebResponse.Json(new { errors = result.Validation.Errors, warnings = result.Validation.Warnings }, 422)
            : WebResponse.RawJson(Strategies.StrategySerialization.Serialize(result.Strategy), 201);
    }

    private WebResponse ExportStrategy(string id)
    {
        if (!_strategies.TryGet(id, out var doc)) return WebResponse.Empty(404);
        var safeName = string.Concat(doc.Identity.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
        if (safeName.Length == 0) safeName = "strategy";
        return WebResponse.RawJson(_strategies.Export(id))
            .WithHeader("Content-Disposition", $"attachment; filename=\"{safeName}.bubbles-strategy.json\"");
    }

    // ── Run history ────────────────────────────────────────────────────────

    private WebResponse RouteRuns(WebRequest req)
    {
        if (req.Method != "GET") return WebResponse.Empty(405);
        var segments = req.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /api/runs  |  /api/runs/summary  |  /api/runs/{runId}
        if (segments.Length == 3)
        {
            if (segments[2].Equals("summary", StringComparison.OrdinalIgnoreCase))
                return WebResponse.Json(_runReports.Summary());
            var id = Uri.UnescapeDataString(segments[2]);
            return _runReports.GetById(id) is { } report ? WebResponse.Json(report) : WebResponse.Empty(404);
        }

        var query = ParseQuery(req.Query);
        var since = TryParseDate(query.GetValueOrDefault("since"));
        var before = TryParseDate(query.GetValueOrDefault("before"));
        var limit = int.TryParse(query.GetValueOrDefault("limit"), out var l) ? l : 100;
        return WebResponse.Json(new { runs = _runReports.Query(since, before, limit), summary = _runReports.Summary() });
    }

    private static DateTime? TryParseDate(string? value)
        => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    // ── HttpListener transport ─────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleHttpAsync(ctx));
        }
    }

    private async Task HandleHttpAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/ws" && ctx.Request.IsWebSocketRequest) { await HandleWebSocketAsync(ctx); return; }

            if (ctx.Request.ContentLength64 > MaxBodyBytes)
            {
                ctx.Response.StatusCode = 413;
                ctx.Response.Close();
                return;
            }
            string body = "";
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                body = await reader.ReadToEndAsync();
                if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
                {
                    ctx.Response.StatusCode = 413;
                    ctx.Response.Close();
                    return;
                }
            }

            var remote = ctx.Request.RemoteEndPoint?.Address;
            var q = ctx.Request.Url?.Query ?? "";
            if (q.StartsWith('?')) q = q[1..];
            var req = new WebRequest(ctx.Request.HttpMethod, path, q, body, remote is not null && IPAddress.IsLoopback(remote));
            var response = await RouteAsync(req);

            ctx.Response.StatusCode = response.Status;
            ctx.Response.ContentType = response.ContentType;
            foreach (var (key, value) in response.Headers) ctx.Response.Headers[key] = value;
            ctx.Response.ContentLength64 = response.Body.Length;
            if (response.Body.Length > 0) await ctx.Response.OutputStream.WriteAsync(response.Body);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            try { ctx.Response.StatusCode = 500; using var sw = new StreamWriter(ctx.Response.OutputStream); sw.Write(ex.Message); }
            catch { }
        }
    }

    // ── Raw-TCP fallback transport (full app, no WebSocket) ─────────────────

    private void StartFallback()
    {
        try
        {
            _fallbackListener = new TcpListener(IPAddress.Loopback, WebPort);
            _fallbackListener.Start();
            _fallbackLoop = Task.Run(FallbackLoopAsync);
            Console.WriteLine($"Web UI (fallback): http://localhost:{WebPort}  (loopback only; live status via polling)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Loopback TCP fallback failed to bind: {ex.Message}");
        }
    }

    private async Task FallbackLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _fallbackListener is not null)
        {
            TcpClient client;
            try { client = await _fallbackListener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleFallbackAsync(client));
        }
    }

    private async Task HandleFallbackAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { await WriteFallbackAsync(stream, WebResponse.Text(400, "bad request")); return; }

                var contentLength = 0;
                while (await reader.ReadLineAsync() is { } header && header.Length > 0)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header["Content-Length:".Length..].Trim(), out contentLength);

                if (contentLength > MaxBodyBytes) { await WriteFallbackAsync(stream, WebResponse.Text(413, "too large")); return; }

                var body = string.Empty;
                if (contentLength > 0)
                {
                    var chars = new char[contentLength];
                    var read = 0;
                    while (read < chars.Length)
                    {
                        var n = await reader.ReadAsync(chars.AsMemory(read, chars.Length - read));
                        if (n == 0) break;
                        read += n;
                    }
                    body = new string(chars, 0, read);
                }

                var rawTarget = parts[1];
                var qIndex = rawTarget.IndexOf('?');
                var path = qIndex >= 0 ? rawTarget[..qIndex] : rawTarget;
                var query = qIndex >= 0 ? rawTarget[(qIndex + 1)..] : "";
                // The fallback is loopback-only by construction (TcpListener bound to loopback).
                var response = await RouteAsync(new WebRequest(parts[0], path, query, body.Trim(), IsLoopback: true));
                await WriteFallbackAsync(stream, response);
            }
            catch { }
        }
    }

    private static async Task WriteFallbackAsync(NetworkStream stream, WebResponse response)
    {
        var reason = response.Status switch
        {
            200 => "OK", 201 => "Created", 204 => "No Content",
            400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            409 => "Conflict", 413 => "Payload Too Large", 422 => "Unprocessable Entity",
            _ => response.Status >= 500 ? "Server Error" : "OK",
        };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {response.Status} {reason}\r\n");
        sb.Append($"Content-Type: {response.ContentType}\r\n");
        sb.Append($"Content-Length: {response.Body.Length}\r\n");
        foreach (var (key, value) in response.Headers) sb.Append($"{key}: {value}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        if (response.Body.Length > 0) await stream.WriteAsync(response.Body);
    }

    // ── WebSocket (HttpListener only) ──────────────────────────────────────

    private async Task HandleWebSocketAsync(HttpListenerContext ctx)
    {
        WebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
        catch { return; }

        var ws = wsCtx.WebSocket;
        lock (_socketsLock) _sockets.Add(ws);

        var buf = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cts.Token);
                    break;
                }
            }
        }
        catch { }
        finally
        {
            lock (_socketsLock) _sockets.Remove(ws);
            try { ws.Dispose(); } catch { }
        }
    }

    private async Task BroadcastLoopAsync()
    {
        var delayMs = 1000 / StatusHz;
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(delayMs);

            List<WebSocket> snapshot;
            lock (_socketsLock) snapshot = _sockets.Where(s => s.State == WebSocketState.Open).ToList();
            if (snapshot.Count == 0) continue;

            byte[] payload;
            try { payload = JsonSerializer.SerializeToUtf8Bytes(_getStatus(), JsonOptions); }
            catch { continue; }

            var seg = new ArraySegment<byte>(payload);
            foreach (var ws in snapshot)
            {
                try { await ws.SendAsync(seg, WebSocketMessageType.Text, endOfMessage: true, _cts.Token); }
                catch { /* dropped on next iteration via the Where filter */ }
            }
        }
    }

    // ── Schema reflection ──────────────────────────────────────────────────

    /// <summary>
    /// Walks <see cref="BotSettings"/> properties (recursively for nested settings classes) and
    /// builds a UI-friendly schema. Each field carries a <c>path</c> array of JSON names so the
    /// frontend resolves nested values generically.
    /// </summary>
    private static object BuildSchema()
    {
        var fields = new List<object>();
        WalkSettingsType(typeof(BotSettings), new List<string>(), fields);
        return new { fields };
    }

    private static void WalkSettingsType(Type type, List<string> path, List<object> fields)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<SettingAttribute>();
            if (attr is null) continue;

            var jsonName = char.ToLower(prop.Name[0]) + prop.Name[1..];

            if (prop.GetCustomAttribute<SettingNestedAttribute>() is not null)
            {
                var childPath = new List<string>(path) { jsonName };
                WalkSettingsType(prop.PropertyType, childPath, fields);
                continue;
            }

            var range    = prop.GetCustomAttribute<SettingRangeAttribute>();
            var keycode  = prop.GetCustomAttribute<SettingKeycodeAttribute>() is not null;
            var options  = prop.GetCustomAttribute<SettingOptionsAttribute>();
            var skills   = prop.GetCustomAttribute<SettingSkillsAttribute>() is not null;
            var flasks   = prop.GetCustomAttribute<SettingFlasksAttribute>() is not null;
            var slist    = prop.GetCustomAttribute<SettingStringListAttribute>();
            var modTable = prop.GetCustomAttribute<SettingModTableAttribute>() is not null;
            var mapModTable = prop.GetCustomAttribute<SettingMapModifierTableAttribute>() is not null;

            var fieldType = mapModTable                                  ? "mapmodtable"
                          : modTable                                     ? "modtable"
                          : flasks                                       ? "flasks"
                          : skills                                       ? "skills"
                          : slist  is not null                           ? "stringlist"
                          : options is not null                          ? "options"
                          : prop.PropertyType == typeof(bool)            ? "bool"
                          : prop.PropertyType == typeof(int) && keycode  ? "keycode"
                          : prop.PropertyType == typeof(int)             ? "int"
                          : prop.PropertyType == typeof(float)           ? "float"
                          : prop.PropertyType == typeof(string)          ? "string"
                          : "unknown";

            var fullPath = new string[path.Count + 1];
            for (var i = 0; i < path.Count; i++) fullPath[i] = path[i];
            fullPath[^1] = jsonName;

            object[]? modsCatalog = null;
            object[]? tierOptions = null;
            object[]? mapModsCatalog = null;
            object[]? policyOptions = null;
            if (modTable)
            {
                modsCatalog = BubblesBot.Core.Knowledge.UltimatumModDanger.KnownMods
                    .Select(m => (object)new { id = m.Id, name = m.DisplayName, defaultDanger = m.DefaultDanger })
                    .ToArray();
                tierOptions = BubblesBot.Core.Knowledge.UltimatumModDanger.Tiers
                    .Select(t => (object)new { label = t.Label, value = t.Value })
                    .ToArray();
            }
            if (mapModTable)
            {
                mapModsCatalog = BubblesBot.Core.Knowledge.MapModifierCatalog.Known
                    .Select(m => (object)new
                    {
                        key = m.Key,
                        category = m.Category,
                        name = m.DisplayName,
                        defaultPolicy = (int)m.DefaultDisposition,
                    })
                    .ToArray();
                policyOptions = BubblesBot.Core.Knowledge.MapModifierCatalog.Policies
                    .Select(p => (object)new { label = p.Label, value = p.Value })
                    .ToArray();
            }

            fields.Add(new
            {
                name        = jsonName,
                path        = fullPath,
                category    = attr.Category,
                displayName = attr.DisplayName,
                description = attr.Description,
                type        = fieldType,
                min         = range?.Min,
                max         = range?.Max,
                step        = range?.Step,
                placeholder = slist?.Placeholder,
                options     = options?.Options.Select(o => new { label = o.Label, value = o.Value }).ToArray(),
                mods        = modsCatalog,
                tiers       = tierOptions,
                mapMods     = mapModsCatalog,
                policies    = policyOptions,
            });
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Copy only properties explicitly exposed by the settings schema.</summary>
    private static void ApplySupportedSettings(object current, object incoming)
    {
        foreach (var prop in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetCustomAttribute<SettingAttribute>() is null)
                continue;

            if (prop.GetCustomAttribute<SettingNestedAttribute>() is not null)
            {
                var currentChild = prop.GetValue(current);
                var incomingChild = prop.GetValue(incoming);
                if (currentChild is not null && incomingChild is not null)
                    ApplySupportedSettings(currentChild, incomingChild);
                continue;
            }

            var value = prop.GetValue(incoming);
            if (value is not null) prop.SetValue(current, value);
        }
    }

    /// <summary>
    /// Find the WebUI/wwwroot directory. In dev runs (dotnet run) the source tree is the working
    /// directory; in published builds the assets ship next to the exe.
    /// </summary>
    private static string ResolveAssetRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, AssetRoot),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "BubblesBot.Bot", AssetRoot),
            Path.Combine(Directory.GetCurrentDirectory(), AssetRoot),
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return candidates[0]; // best-effort; individual asset reads 404
    }
}
