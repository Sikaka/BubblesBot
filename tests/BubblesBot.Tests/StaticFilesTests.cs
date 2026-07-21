using BubblesBot.Bot.Web;

namespace BubblesBot.Tests;

public sealed class StaticFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "BubblesBot.Tests", Guid.NewGuid().ToString("N"));

    public StaticFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<div id=root>");
        File.WriteAllText(Path.Combine(_root, "assets", "index-ABC123.js"), "js");
        File.WriteAllText(Path.Combine(_root, "assets", "index-ABC123.css"), "css");
        File.WriteAllText(Path.Combine(_root, "secret.pfx"), "not servable");
    }

    [Fact]
    public void RootServesIndexHtmlWithNoCache()
    {
        var asset = StaticFiles.Resolve(_root, "/");
        Assert.NotNull(asset);
        Assert.EndsWith("index.html", asset.FilePath);
        Assert.Equal(StaticFiles.NoCacheControl, asset.CacheControl);
        Assert.StartsWith("text/html", asset.ContentType);
    }

    [Fact]
    public void HashedAssetsAreImmutable()
    {
        var js = StaticFiles.Resolve(_root, "/assets/index-ABC123.js");
        Assert.NotNull(js);
        Assert.Equal(StaticFiles.ImmutableCacheControl, js.CacheControl);
        Assert.StartsWith("application/javascript", js.ContentType);

        var css = StaticFiles.Resolve(_root, "/assets/index-ABC123.css");
        Assert.NotNull(css);
        Assert.Equal(StaticFiles.ImmutableCacheControl, css.CacheControl);
    }

    [Fact]
    public void ExtensionLessPathsFallBackToIndexForSpaRouting()
    {
        foreach (var route in new[] { "/settings", "/strategies/abc123", "/setup" })
        {
            var asset = StaticFiles.Resolve(_root, route);
            Assert.NotNull(asset);
            Assert.EndsWith("index.html", asset.FilePath);
            Assert.Equal(StaticFiles.NoCacheControl, asset.CacheControl);
        }
    }

    [Fact]
    public void ApiAndWsPathsAreNeverServed()
    {
        Assert.Null(StaticFiles.Resolve(_root, "/api/status"));
        Assert.Null(StaticFiles.Resolve(_root, "/api/anything/nested"));
        Assert.Null(StaticFiles.Resolve(_root, "/ws"));
    }

    [Fact]
    public void TraversalAttemptsAreRejected()
    {
        Assert.Null(StaticFiles.Resolve(_root, "/../secret.txt"));
        Assert.Null(StaticFiles.Resolve(_root, "/assets/../../secret.txt"));
        Assert.Null(StaticFiles.Resolve(_root, "/..%2F..%2Fsecret.txt"));
    }

    [Fact]
    public void UnknownExtensionsAreRejectedEvenWhenTheFileExists()
    {
        Assert.Null(StaticFiles.Resolve(_root, "/secret.pfx"));
    }

    [Fact]
    public void MissingFileWithExtensionIs404NotSpaFallback()
    {
        Assert.Null(StaticFiles.Resolve(_root, "/assets/gone-XYZ.js"));
    }

    [Fact]
    public void MissingRootDirectoryResolvesNothing()
    {
        Assert.Null(StaticFiles.Resolve(Path.Combine(_root, "does-not-exist"), "/"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
