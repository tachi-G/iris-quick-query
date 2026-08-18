using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;

namespace IrisQuickQuery.Core.Tests;

public sealed class AcceptanceFlowTests
{
    [Fact]
    public async Task IdCard_ToRegistration_ToHiddenId_ToVisitSelection_ToDates()
    {
        var config = TestConfig.CreateElements("id_card_no", "registration_no", "internal_id", "visit_no", "admission_time", "discharge_time");
        config.Elements.First(x => x.Key == "internal_id").Role = ElementRole.Hidden;
        var dates = new QueryRuleDefinition
        {
            Name = "就诊时间", DisplayOrder = 1, SqlTemplate = "SELECT admission_time,discharge_time FROM T WHERE visit={{visit_no}}",
            OutputMappings =
            [
                new() { ColumnName = "admission_time", ElementKey = "admission_time" },
                new() { ColumnName = "discharge_time", ElementKey = "discharge_time" }
            ]
        };
        var visits = TestConfig.Rule("就诊列表", "internal_id", "visit_no", RuleResultMode.List, 2);
        var internalId = TestConfig.Rule("内部ID", "registration_no", "internal_id", order: 3);
        var registration = TestConfig.Rule("登记号", "id_card_no", "registration_no", order: 4);
        config.Rules.AddRange([dates, visits, internalId, registration]);
        var fake = new FakeExecutor
        {
            [registration.Id] = _ => One(("registration_no", "R-001")),
            [internalId.Id] = _ => One(("internal_id", "P-88")),
            [visits.Id] = _ => [Row(("visit_no", "V-1")), Row(("visit_no", "V-2"))],
            [dates.Id] = request => One(
                ("admission_time", request.Parameters[0].Value + "-IN"),
                ("discharge_time", request.Parameters[0].Value + "-OUT"))
        };
        var context = new RunContext(config, new Dictionary<string, object?> { ["id_card_no"] = "110101199001010000" });
        var scheduler = new RuleScheduler(fake);

        Assert.Equal(QueryRunStatus.WaitingForSelection, await scheduler.RunUntilStableAsync(config, context, CancellationToken.None));
        Assert.Equal("R-001", context.Slots["registration_no"].EffectiveValue);
        Assert.Equal("P-88", context.Slots["internal_id"].EffectiveValue);
        context.SelectListRow(context.ListResults.Single().ExecutionId, 1);
        Assert.Equal(QueryRunStatus.Completed, await scheduler.RunUntilStableAsync(config, context, CancellationToken.None));
        Assert.Equal("V-2-IN", context.Slots["admission_time"].EffectiveValue);
        Assert.Equal("V-2-OUT", context.Slots["discharge_time"].EffectiveValue);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> One(params (string Key, object? Value)[] values) => [Row(values)];
    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values)
        => values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
}
