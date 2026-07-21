using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubblesBot.Bot.Updates;

public sealed record UpdateSnapshot(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string State,
    DateTime? CheckedAtUtc);

/// <summary>
/// Lightweight, non-blocking release check. The public GitHub endpoint requires no token; checks
/// run at startup and at most once every six hours, with a shorter retry after transient failure.
/// </summary>
internal sealed class GitHubReleaseUpdateChecker
{
    internal const string ReleasesPageUrl = "https://github.com/Sikaka/BubblesBot/releases/latest";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/Sikaka/BubblesBot/releases/latest";
    private static readonly TimeSpan SuccessInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureInterval = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly object _sync = new();
    private readonly string _currentVersion = ReleaseVersion.CurrentDisplayVersion();
    private UpdateSnapshot _snapshot;
    private DateTime _nextCheckUtc = DateTime.MinValue;
    private bool _checking;

    public GitHubReleaseUpdateChecker()
    {
        _snapshot = new UpdateSnapshot(_currentVersion, null, false, null, "checking", null);
    }

    public UpdateSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public void RefreshIfDue()
    {
        lock (_sync)
        {
            if (_checking || DateTime.UtcNow < _nextCheckUtc) return;
            _checking = true;
            if (_snapshot.CheckedAtUtc is null)
                _snapshot = _snapshot with { State = "checking" };
        }

        _ = Task.Run(CheckAsync);
    }

    private async Task CheckAsync()
    {
        var succeeded = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Publish(new UpdateSnapshot(_currentVersion, null, false, ReleasesPageUrl, "no-release", DateTime.UtcNow));
                succeeded = true;
                return;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<LatestReleaseDto>(stream).ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                throw new JsonException("latest release response did not contain a tag name");

            var latest = release.TagName.Trim();
            var available = ReleaseVersion.IsNewer(latest, _currentVersion);
            var releaseUrl = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    ? parsed.ToString()
                    : ReleasesPageUrl;
            Publish(new UpdateSnapshot(
                _currentVersion,
                latest,
                available,
                releaseUrl,
                available ? "available" : "current",
                DateTime.UtcNow));
            succeeded = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Publish(new UpdateSnapshot(_currentVersion, null, false, ReleasesPageUrl, "unavailable", DateTime.UtcNow));
        }
        finally
        {
            lock (_sync)
            {
                _checking = false;
                _nextCheckUtc = DateTime.UtcNow + (succeeded ? SuccessInterval : FailureInterval);
            }
        }
    }

    private void Publish(UpdateSnapshot snapshot)
    {
        lock (_sync) _snapshot = snapshot;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BubblesBot", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private sealed class LatestReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

internal static class ReleaseVersion
{
    public static string CurrentDisplayVersion()
    {
        var assembly = typeof(ReleaseVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var value = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;
        var metadata = value.IndexOf('+');
        return metadata >= 0 ? value[..metadata] : value;
    }

    public static bool IsNewer(string candidate, string current)
        => TryParse(candidate, out var candidateVersion)
            && TryParse(current, out var currentVersion)
            && candidateVersion.CompareTo(currentVersion) > 0;

    internal static bool TryParse(string value, out ComparableVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var core = value.Trim().TrimStart('v', 'V');
        var suffix = core.IndexOfAny(['-', '+']);
        if (suffix >= 0) core = core[..suffix];
        var parts = core.Split('.');
        if (parts.Length is < 2 or > 4) return false;

        Span<int> numbers = stackalloc int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return false;
        version = new ComparableVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    internal readonly record struct ComparableVersion(int Major, int Minor, int Patch, int Revision)
        : IComparable<ComparableVersion>
    {
        public int CompareTo(ComparableVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor);
            if (minor != 0) return minor;
            var patch = Patch.CompareTo(other.Patch);
            return patch != 0 ? patch : Revision.CompareTo(other.Revision);
        }
    }
}
