using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;

namespace IrisQuickQuery.Core.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task UnorderedRules_ReachStableClosure()
    {
        var config = TestConfig.CreateElements("a", "b", "c");
        var bToC = TestConfig.Rule("b-to-c", "b", "c", order: 1);
        var aToB = TestConfig.Rule("a-to-b", "a", "b", order: 2);
        config.Rules.AddRange([bToC, aToB]);
        var fake = new FakeExecutor
        {
            [aToB.Id] = _ => Rows(("b", "B-1")),
            [bToC.Id] = _ => Rows(("c", "C-1"))
        };
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A-1" });

        var status = await new RuleScheduler(fake).RunUntilStableAsync(config, context, CancellationToken.None);

        Assert.Equal(QueryRunStatus.Completed, status);
        Assert.Equal("B-1", context.Slots["b"].EffectiveValue);
        Assert.Equal("C-1", context.Slots["c"].EffectiveValue);
        Assert.Equal([aToB.Id, bToC.Id], fake.Calls);
    }

    [Fact]
    public async Task BidirectionalCycle_ExecutesEachInputFingerprintOnce()
    {
        var config = TestConfig.CreateElements("a", "b");
        var aToB = TestConfig.Rule("a-to-b", "a", "b");
        var bToA = TestConfig.Rule("b-to-a", "b", "a");
        config.Rules.AddRange([aToB, bToA]);
        var fake = new FakeExecutor
        {
            [aToB.Id] = _ => Rows(("b", "stable")),
            [bToA.Id] = _ => Rows(("a", "seed"))
        };
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "seed" });

        await new RuleScheduler(fake).RunUntilStableAsync(config, context, CancellationToken.None);

        Assert.Equal(2, context.ExecutedRuleCount);
        Assert.Equal(ElementRuntimeState.Manual, context.Slots["a"].State);
        Assert.Equal(ElementRuntimeState.Derived, context.Slots["b"].State);
    }

    [Fact]
    public async Task ConflictingOutputs_BlockDependentRule()
    {
        var config = TestConfig.CreateElements("a", "b", "c");
        var first = TestConfig.Rule("first", "a", "b", order: 1);
        var second = TestConfig.Rule("second", "a", "b", order: 2);
        var downstream = TestConfig.Rule("downstream", "b", "c", order: 3);
        config.Rules.AddRange([first, second, downstream]);
        var fake = new FakeExecutor
        {
            [first.Id] = _ => Rows(("b", "B-1")),
            [second.Id] = _ => Rows(("b", "B-2")),
            [downstream.Id] = _ => Rows(("c", "should-not-run"))
        };
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A" });

        var status = await new RuleScheduler(fake).RunUntilStableAsync(config, context, CancellationToken.None);

        Assert.Equal(QueryRunStatus.CompletedWithErrors, status);
        Assert.Equal(ElementRuntimeState.Conflict, context.Slots["b"].State);
        Assert.DoesNotContain(downstream.Id, fake.Calls);
        Assert.Equal(1, context.BlockedRuleCount);
    }

    [Fact]
    public async Task ScalarRuleWithMultipleRows_FailsWithoutChoosingFirst()
    {
        var config = TestConfig.CreateElements("a", "b");
        var rule = TestConfig.Rule("scalar", "a", "b"); config.Rules.Add(rule);
        var fake = new FakeExecutor { [rule.Id] = _ => [Row(("b", "1")), Row(("b", "2"))] };
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A" });

        await new RuleScheduler(fake).RunUntilStableAsync(config, context, CancellationToken.None);

        Assert.Equal(RuleExecutionStatus.Failed, context.Executions.Single().Status);
        Assert.Null(context.Slots["b"].EffectiveValue);
    }

    [Fact]
    public async Task SingleListRow_AutoSelectsAndContinuesDownstream()
    {
        var config = TestConfig.CreateElements("a", "visit", "detail");
        var list = TestConfig.Rule("visits", "a", "visit", RuleResultMode.List, 1);
        var detail = TestConfig.Rule("detail", "visit", "detail", order: 2);
        config.Rules.AddRange([list, detail]);
        var fake = new FakeExecutor
        {
            [list.Id] = _ => Rows(("visit", "V1")),
            [detail.Id] = _ => Rows(("detail", "D1"))
        };
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A" });

        await new RuleScheduler(fake).RunUntilStableAsync(config, context, CancellationToken.None);

        Assert.Equal(0, context.ListResults.Single().SelectedIndex);
        Assert.Equal("D1", context.Slots["detail"].EffectiveValue);
    }

    [Fact]
    public async Task MultiRowList_WaitsThenSelectionResumes()
    {
        var config = TestConfig.CreateElements("a", "visit", "detail");
        var list = TestConfig.Rule("visits", "a", "visit", RuleResultMode.List, 1);
        var detail = TestConfig.Rule("detail", "visit", "detail", order: 2);
        config.Rules.AddRange([list, detail]);
        var fake = new FakeExecutor
        {
            [list.Id] = _ => [Row(("visit", "V1")), Row(("visit", "V2"))],
            [detail.Id] = request => Rows(("detail", "D-" + request.Parameters[0].Value))
        };
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A" });
        var scheduler = new RuleScheduler(fake);

        Assert.Equal(QueryRunStatus.WaitingForSelection, await scheduler.RunUntilStableAsync(config, context, CancellationToken.None));
        Assert.True(context.SelectListRow(context.ListResults.Single().ExecutionId, 1));
        Assert.Equal(QueryRunStatus.Completed, await scheduler.RunUntilStableAsync(config, context, CancellationToken.None));
        Assert.Equal("D-V2", context.Slots["detail"].EffectiveValue);
    }

    [Fact]
    public async Task ChangingListSelection_RemovesOldBranchAndRecomputesWithoutConflict()
    {
        var config = TestConfig.CreateElements("a", "visit", "detail");
        var list = TestConfig.Rule("visits", "a", "visit", RuleResultMode.List, 1);
        var detail = TestConfig.Rule("detail", "visit", "detail", order: 2);
        config.Rules.AddRange([list, detail]);
        var fake = new FakeExecutor
        {
            [list.Id] = _ => [Row(("visit", "V1")), Row(("visit", "V2"))],
            [detail.Id] = request => Rows(("detail", "D-" + request.Parameters[0].Value))
        };
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A" });
        var scheduler = new RuleScheduler(fake);
        await scheduler.RunUntilStableAsync(config, context, CancellationToken.None);
        var executionId = context.ListResults.Single().ExecutionId;
        context.SelectListRow(executionId, 0);
        await scheduler.RunUntilStableAsync(config, context, CancellationToken.None);
        Assert.Equal("D-V1", context.Slots["detail"].EffectiveValue);

        context.SelectListRow(executionId, 1);
        await scheduler.RunUntilStableAsync(config, context, CancellationToken.None);

        Assert.Equal("D-V2", context.Slots["detail"].EffectiveValue);
        Assert.Equal(ElementRuntimeState.Derived, context.Slots["detail"].State);
        Assert.Equal(2, fake.Calls.Count(x => x == detail.Id));
    }

    [Fact]
    public async Task TruncatedResult_FailsCurrentRule()
    {
        var config = TestConfig.CreateElements("a", "b");
        var rule = TestConfig.Rule("limited", "a", "b"); config.Rules.Add(rule);
        var executor = new TruncatedExecutor();
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A" });
        await new RuleScheduler(executor).RunUntilStableAsync(config, context, CancellationToken.None);
        Assert.Equal("row-limit", context.Executions.Single().ErrorCategory);
        Assert.Null(context.Slots["b"].EffectiveValue);
    }

    [Fact]
    public async Task Cancellation_MarksRunCancelled()
    {
        var config = TestConfig.CreateElements("a", "b");
        config.Rules.Add(TestConfig.Rule("slow", "a", "b"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var context = new RunContext(config, new Dictionary<string, object?> { ["a"] = "A" });
        var status = await new RuleScheduler(new SlowExecutor()).RunUntilStableAsync(config, context, cancellation.Token);
        Assert.Equal(QueryRunStatus.Cancelled, status);
        Assert.Equal(RuleExecutionStatus.Cancelled, context.Executions.Single().Status);
    }

    [Fact]
    public async Task GlobalExecutionLimit_StopsAfterOneHundredRules()
    {
        var keys = Enumerable.Range(0, 102).Select(x => "e" + x).ToArray();
        var config = TestConfig.CreateElements(keys);
        var fake = new FakeExecutor();
        for (var i = 0; i < 101; i++)
        {
            var rule = TestConfig.Rule("r" + i, "e" + i, "e" + (i + 1), order: i);
            config.Rules.Add(rule);
            var output = "e" + (i + 1);
            fake[rule.Id] = _ => Rows((output, "value" + i));
        }
        var context = new RunContext(config, new Dictionary<string, object?> { ["e0"] = "seed" });
        var status = await new RuleScheduler(fake).RunUntilStableAsync(config, context, CancellationToken.None);
        Assert.Equal(QueryRunStatus.LimitReached, status);
        Assert.Equal(100, context.ExecutedRuleCount);
        Assert.Null(context.Slots["e101"].EffectiveValue);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params (string Key, object? Value)[] values) => [Row(values)];
    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values)
        => values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
}

internal sealed class FakeExecutor : IDatabaseQueryExecutor
{
    private readonly Dictionary<Guid, Func<QueryExecutionRequest, IReadOnlyList<IReadOnlyDictionary<string, object?>>>> _handlers = [];
    public List<Guid> Calls { get; } = [];
    public Func<QueryExecutionRequest, IReadOnlyList<IReadOnlyDictionary<string, object?>>> this[Guid id] { set => _handlers[id] = value; }
    public Task<QueryExecutionResult> ExecuteAsync(QueryExecutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls.Add(request.Rule.Id);
        return Task.FromResult(new QueryExecutionResult(_handlers[request.Rule.Id](request)));
    }
}

internal sealed class TruncatedExecutor : IDatabaseQueryExecutor
{
    public Task<QueryExecutionResult> ExecuteAsync(QueryExecutionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new QueryExecutionResult([], true));
}

internal sealed class SlowExecutor : IDatabaseQueryExecutor
{
    public async Task<QueryExecutionResult> ExecuteAsync(QueryExecutionRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new QueryExecutionResult([]);
    }
}
