using System.Text.Json;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;

namespace BubblesBot.Research.Probing;

public sealed record ProbeExecutionArtifact(
    string Name,
    string Group,
    string Description,
    ProbeStatus Status,
    string Evidence,
    IReadOnlyList<OffsetCandidate> Candidates,
    ProbeResult? Discovery);

/// <summary>Persists every probe verdict as a machine-readable, patch-versioned artifact.</summary>
public static class ProbeArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Write(ProbeContext context, IReadOnlyList<ProbeExecutionArtifact> results)
    {
        var directory = ResolveArtifactDirectory();
        Directory.CreateDirectory(directory);
        // Multiple read-only probes are often launched together against the same live
        // situation. Millisecond timestamps can collide, so include a short nonce and use
        // CreateNew semantics rather than allowing one capture to overwrite another.
        var path = Path.Combine(directory,
            $"probe-run-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}"[..55] + ".json");
        var artifact = new
        {
            formatVersion = 1,
            capturedUtc = DateTime.UtcNow,
            gameBuild = context.GameBuild,
            offsetCatalogVersion = MemorySchema.OffsetCatalogVersion,
            observationSemanticsVersion = MemorySchema.ObservationSemanticsVersion,
            dispositionCatalogVersion = EntityDispositionCatalog.Version,
            oracleAvailable = context.Oracle.IsAvailable,
            baselineCapturedUtc = context.Facts.CapturedUtc,
            baselineFactCount = context.Facts.Facts.Count,
            results,
        };
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream))
            writer.Write(JsonSerializer.Serialize(artifact, JsonOptions));
        Console.WriteLine($"probe artifact: {path}");
        return path;
    }

    private static string ResolveArtifactDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubblesBot", "research", "probe-artifacts");
}
