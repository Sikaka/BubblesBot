using BubblesBot.Bot.Updates;

namespace BubblesBot.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("v1.2.0", "1.2.1", false)]
    [InlineData("v2.0.0", "1.99.99", true)]
    [InlineData("v1.2.3", "1.2.3+build.7", false)]
    [InlineData("v1.2.4", "1.2.3-local", true)]
    [InlineData("not-a-version", "1.2.3", false)]
    public void ComparesReleaseTags(string candidate, string current, bool expected)
        => Assert.Equal(expected, ReleaseVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("2.0.0-beta.1")]
    public void ParsesSupportedVersions(string value)
        => Assert.True(ReleaseVersion.TryParse(value, out _));
}
