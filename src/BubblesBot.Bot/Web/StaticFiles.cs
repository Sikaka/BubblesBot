namespace BubblesBot.Bot.Web;

/// <summary>
/// Static-asset resolution for the SPA build output in <c>WebUI/wwwroot</c>. Pure path →
/// file mapping so it is unit-testable without a listener: traversal guard, MIME typing,
/// cache policy (Vite's content-hashed <c>/assets/*</c> files are immutable; everything
/// else — notably <c>index.html</c> — must revalidate), and index.html fallback for
/// extension-less paths so client-side routes deep-link correctly.
/// </summary>
public static class StaticFiles
{
    /// <summary>Vite emits content-hashed filenames under /assets — new content means a new URL.</summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string NoCacheControl = "no-cache";

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"]  = "text/html; charset=utf-8",
        [".js"]    = "application/javascript; charset=utf-8",
        [".mjs"]   = "application/javascript; charset=utf-8",
        [".css"]   = "text/css; charset=utf-8",
        [".json"]  = "application/json; charset=utf-8",
        [".map"]   = "application/json; charset=utf-8",
        [".svg"]   = "image/svg+xml",
        [".png"]   = "image/png",
        [".jpg"]   = "image/jpeg",
        [".gif"]   = "image/gif",
        [".ico"]   = "image/x-icon",
        [".woff"]  = "font/woff",
        [".woff2"] = "font/woff2",
        [".txt"]   = "text/plain; charset=utf-8",
    };

    public sealed record ResolvedAsset(string FilePath, string ContentType, string CacheControl);

    /// <summary>
    /// Map a URL path to a servable file inside <paramref name="assetRoot"/>, or null for
    /// 404 (API-shaped paths, traversal attempts, unknown extensions, missing files).
    /// </summary>
    public static ResolvedAsset? Resolve(string assetRoot, string urlPath)
    {
        if (string.IsNullOrEmpty(assetRoot) || !Directory.Exists(assetRoot)) return null;
        if (urlPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || urlPath.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = Uri.UnescapeDataString(urlPath).TrimStart('/');
        if (relative.Length == 0) relative = "index.html";
        if (relative.Contains('\0')) return null;

        var rootFull = Path.GetFullPath(assetRoot);
        string fileFull;
        try { fileFull = Path.GetFullPath(Path.Combine(rootFull, relative)); }
        catch { return null; }
        if (!fileFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fileFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            return null;   // traversal attempt

        var extension = Path.GetExtension(fileFull);
        if (extension.Length > 0)
        {
            if (!File.Exists(fileFull) || !MimeByExtension.TryGetValue(extension, out var mime))
                return null;
            var immutable = relative.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                         || relative.StartsWith("assets\\", StringComparison.OrdinalIgnoreCase);
            return new ResolvedAsset(fileFull, mime, immutable ? ImmutableCacheControl : NoCacheControl);
        }

        // Extension-less path → SPA fallback. The client router owns /settings, /strategies/….
        var index = Path.Combine(rootFull, "index.html");
        return File.Exists(index)
            ? new ResolvedAsset(index, MimeByExtension[".html"], NoCacheControl)
            : null;
    }
}
