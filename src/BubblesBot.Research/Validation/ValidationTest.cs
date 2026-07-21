using BubblesBot.Core;

namespace BubblesBot.Research.Validation;

/// <summary>
/// One regression test. Each test reads something from the live game two ways â€” through our reader
/// and through POEMCP's `/eval` (which calls into ExileCore) â€” and asserts they agree.
///
/// The full set of these IS our per-patch regression check: after a PoE update, run them all,
/// whatever fails is what needs offset-table maintenance.
/// </summary>
public abstract class ValidationTest
{
    public abstract string Name { get; }
    public virtual string? Group => null;

    public abstract Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct);
}

public sealed class TestContext
{
    public required ProcessHandle Process { get; init; }
    public required MemoryReader Reader { get; init; }
    public required PoemcpClient Poemcp { get; init; }

    /// <summary>Shared scratchpad for inter-test state â€” tests can populate this so later tests don't re-derive anchors.</summary>
    public Dictionary<string, object> State { get; } = new();
}

public abstract record TestOutcome(string Name, string Detail)
{
    public sealed record Pass(string TestName, string Message) : TestOutcome(TestName, Message);
    public sealed record Fail(string TestName, string Message) : TestOutcome(TestName, Message);
    public sealed record Skip(string TestName, string Reason) : TestOutcome(TestName, Reason);
}

public static class TestRunner
{
    public static async Task<int> RunAllAsync(IEnumerable<ValidationTest> tests, TestContext ctx, CancellationToken ct = default)
    {
        var groups = tests.GroupBy(t => t.Group ?? "ungrouped").ToList();
        var results = new List<TestOutcome>();

        foreach (var group in groups)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {group.Key} ---");
            foreach (var test in group)
            {
                Console.Write($"  {test.Name,-50} ");
                TestOutcome outcome;
                try
                {
                    outcome = await test.RunAsync(ctx, ct);
                }
                catch (Exception ex)
                {
                    outcome = new TestOutcome.Fail(test.Name, $"threw {ex.GetType().Name}: {ex.Message}");
                }

                results.Add(outcome);

                switch (outcome)
                {
                    case TestOutcome.Pass p: Console.WriteLine($"PASS  {p.Detail}"); break;
                    case TestOutcome.Fail f: Console.WriteLine($"FAIL  {f.Detail}"); break;
                    case TestOutcome.Skip s: Console.WriteLine($"SKIP  {s.Detail}"); break;
                }
            }
        }

        var passed = results.OfType<TestOutcome.Pass>().Count();
        var failed = results.OfType<TestOutcome.Fail>().Count();
        var skipped = results.OfType<TestOutcome.Skip>().Count();
        Console.WriteLine();
        Console.WriteLine($"Summary: {passed} passed, {failed} failed, {skipped} skipped (of {results.Count})");
        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var f in results.OfType<TestOutcome.Fail>())
                Console.WriteLine($"  {f.Name}: {f.Detail}");
        }
        return failed == 0 ? 0 : 1;
    }
}
