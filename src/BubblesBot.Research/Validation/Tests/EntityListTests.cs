using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Reads the EntityList wrapper at IngameData+0x9A0. This is a tree of
/// Entity-wrapper nodes. We only sanity-check the wrapper here; full traversal
/// lives in CoreSnapshotTests.
/// </summary>
public sealed class EntityListBasicReadTest : ValidationTest
{
    public override string Name => "EntityList wrapper at IngameData+0x9A0";
    public override string? Group => "EntityList";

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.EntityList, out var elObj) || elObj is not nint el)
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "EntityList address not resolved"));

        if (!ctx.Reader.TryReadStruct<nint>(el + KnownOffsets.EntityList.FirstAddr, out var first))
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "FirstAddr read failed"));
        if (!ctx.Reader.TryReadStruct<nint>(el + KnownOffsets.EntityList.Root, out var root))
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "Root read failed"));
        if (!ctx.Reader.TryReadStruct<byte>(el + KnownOffsets.EntityList.IsEmpty, out var isEmptyByte))
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "IsEmpty read failed"));

        if (first == 0 || (long)first < 0x10000)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, $"FirstAddr 0x{first:X} doesn't look like a valid pointer"));
        if (root == 0 || (long)root < 0x10000)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, $"Root 0x{root:X} doesn't look like a valid pointer"));

        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(
            Name,
            $"FirstAddr=0x{first:X16}, Root=0x{root:X16}, IsEmptyByte=0x{isEmptyByte:X2}"));
    }
}
