using System.Text.Json;
using BubblesBot.Bot.Diagnostics;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: BubblesBot.Replay <recording.jsonl|directory> [--tick N]");
    return 1;
}

long? stopAt = null;
for (var i = 1; i + 1 < args.Length; i++)
    if (args[i] == "--tick" && long.TryParse(args[i + 1], out var tick)) stopAt = tick;

try
{
    var intents = FlightReplay.Run(args[0], stopAt);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        source = Path.GetFullPath(args[0]),
        stopAtTick = stopAt,
        frames = intents.Count,
        final = intents.LastOrDefault(),
        intents,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Replay failed: {ex.Message}");
    return 2;
}
