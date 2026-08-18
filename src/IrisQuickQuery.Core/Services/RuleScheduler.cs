using System.Diagnostics;
using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public sealed class RuleScheduler
{
    public const int GlobalTimeoutSeconds = 15;
    public const int GlobalMaxRows = 500;
    public const int GlobalMaxExecutions = 100;

    private readonly IDatabaseQueryExecutor _executor;
    private readonly IExecutionMetadataSink _metadataSink;

    public RuleScheduler(IDatabaseQueryExecutor executor, IExecutionMetadataSink? metadataSink = null)
    {
        _executor = executor;
        _metadataSink = metadataSink ?? new NullExecutionMetadataSink();
    }

    public async Task<QueryRunStatus> RunUntilStableAsync(ConfigurationSnapshot snapshot, RunContext context, CancellationToken cancellationToken)
    {
        context.CurrentSnapshot = snapshot;
        context.Status = QueryRunStatus.Running;
        context.OnChanged();
        try
        {
            while (context.ExecutedRuleCount < GlobalMaxExecutions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ready = FindNextReadyRule(snapshot, context);
                if (ready is null) break;
                await ExecuteRuleAsync(snapshot, context, ready.Rule, ready.Compiled, ready.Parameters,
                    ready.Fingerprint, ready.Dependencies, cancellationToken).ConfigureAwait(false);
            }

            if (context.ExecutedRuleCount >= GlobalMaxExecutions && FindNextReadyRule(snapshot, context) is not null)
                context.Status = QueryRunStatus.LimitReached;
            else if (context.ListResults.Any(x => x.Rows.Count > 1 && x.SelectedIndex is null))
                context.Status = QueryRunStatus.WaitingForSelection;
            else if (context.Executions.Any(x => x.Status == RuleExecutionStatus.Failed)
                     || context.Slots.Values.Any(x => x.State is ElementRuntimeState.Conflict or ElementRuntimeState.Error))
                context.Status = QueryRunStatus.CompletedWithErrors;
            else
                context.Status = QueryRunStatus.Completed;
            context.BlockedRuleCount = CountBlockedRules(snapshot, context);
        }
        catch (OperationCanceledException)
        {
            context.Status = QueryRunStatus.Cancelled;
            foreach (var running in context.Executions.Where(x => x.Status == RuleExecutionStatus.Running))
                running.Status = RuleExecutionStatus.Cancelled;
        }
        finally { context.OnChanged(); }
        return context.Status;
    }

    private static ReadyRule? FindNextReadyRule(ConfigurationSnapshot snapshot, RunContext context)
    {
        foreach (var rule in snapshot.Rules.Where(x => x.IsEnabled).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id))
        {
            CompiledSql compiled;
            try { compiled = SqlTemplateCompiler.Compile(rule.SqlTemplate); }
            catch { continue; }
            if (!context.TryGetReadyInputs(rule, compiled, out var parameters, out var fingerprint, out var dependencies)) continue;
            if (context.HasExecuted(rule.Id, fingerprint)) continue;
            return new ReadyRule(rule, compiled, parameters, fingerprint, dependencies);
        }
        return null;
    }

    private async Task ExecuteRuleAsync(ConfigurationSnapshot snapshot, RunContext context, QueryRuleDefinition rule,
        CompiledSql compiled, IReadOnlyList<QueryParameter> parameters, string fingerprint, HashSet<string> dependencies,
        CancellationToken cancellationToken)
    {
        var record = new RuleExecutionRecord
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            InputFingerprint = fingerprint,
            InputDependencyTokens = dependencies,
            Status = RuleExecutionStatus.Running
        };
        context.Executions.Add(record);
        context.ExecutedRuleCount++;
        context.OnChanged();
        var watch = Stopwatch.StartNew();
        try
        {
            var request = new QueryExecutionRequest(rule, compiled.CommandText, parameters,
                Math.Clamp(rule.TimeoutSeconds, 1, GlobalTimeoutSeconds), Math.Clamp(rule.MaxRows, 1, GlobalMaxRows));
            var result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            record.RowCount = result.Rows.Count;
            if (result.WasTruncated)
            {
                record.Status = RuleExecutionStatus.Failed;
                record.ErrorCategory = "row-limit";
                record.Message = $"结果超过 {request.MaxRows} 行上限。";
            }
            else if (rule.ResultMode == RuleResultMode.Scalar)
            {
                HandleScalar(context, rule, record, result.Rows);
            }
            else
            {
                HandleList(context, rule, record, result.Rows);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            record.Status = RuleExecutionStatus.Cancelled;
            record.ErrorCategory = "cancelled";
            throw;
        }
        catch (TimeoutException ex)
        {
            record.Status = RuleExecutionStatus.Failed;
            record.ErrorCategory = "timeout";
            record.Message = ex.Message;
        }
        catch (Exception ex)
        {
            record.Status = RuleExecutionStatus.Failed;
            record.ErrorCategory = "database";
            record.Message = SanitizeError(ex.Message);
        }
        finally
        {
            watch.Stop();
            record.Duration = watch.Elapsed;
            try
            {
                await _metadataSink.WriteAsync(new ExecutionMetadata(DateTimeOffset.UtcNow, context.RunId, rule.Id,
                    record.Status, (long)record.Duration.TotalMilliseconds, record.RowCount, record.ErrorCategory), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 诊断日志不可用时不能改变查询结果或中断规则调度。
            }
            context.OnChanged();
        }
    }

    private static void HandleScalar(RunContext context, QueryRuleDefinition rule, RuleExecutionRecord record,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0) { record.Status = RuleExecutionStatus.NoResult; return; }
        if (rows.Count > 1)
        {
            record.Status = RuleExecutionStatus.Failed;
            record.ErrorCategory = "cardinality";
            record.Message = "单记录规则返回了多行。";
            return;
        }
        record.Status = RuleExecutionStatus.Success;
        context.AddMappedRow(rule, record, rows[0], record.SourceToken, record.InputDependencyTokens);
    }

    private static void HandleList(RunContext context, QueryRuleDefinition rule, RuleExecutionRecord record,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0) { record.Status = RuleExecutionStatus.NoResult; return; }
        var list = new ListResultState
        {
            ExecutionId = record.ExecutionId,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Rows = rows,
            Mappings = rule.OutputMappings
        };
        context.ListResults.Add(list);
        if (rows.Count == 1)
        {
            record.Status = RuleExecutionStatus.Success;
            context.SelectListRow(record.ExecutionId, 0);
        }
        else record.Status = RuleExecutionStatus.WaitingForSelection;
    }

    private static string SanitizeError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "数据库查询失败。";
        return message.Length <= 800 ? message : message[..800];
    }

    private static int CountBlockedRules(ConfigurationSnapshot snapshot, RunContext context)
    {
        var failedOutputs = context.Executions.Where(x => x.Status == RuleExecutionStatus.Failed)
            .SelectMany(x => snapshot.Rules.FirstOrDefault(r => r.Id == x.RuleId)?.OutputMappings ?? [])
            .Select(x => x.ElementKey)
            .Concat(context.Slots.Where(x => x.Value.State is ElementRuntimeState.Conflict or ElementRuntimeState.Error).Select(x => x.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<Guid>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var rule in snapshot.Rules.Where(x => x.IsEnabled && !context.Executions.Any(e => e.RuleId == x.Id) && !blocked.Contains(x.Id)))
            {
                CompiledSql compiled;
                try { compiled = SqlTemplateCompiler.Compile(rule.SqlTemplate); } catch { continue; }
                if (!compiled.ParameterKeys.Any(failedOutputs.Contains)) continue;
                blocked.Add(rule.Id);
                foreach (var output in rule.OutputMappings) failedOutputs.Add(output.ElementKey);
                changed = true;
            }
        }
        return blocked.Count;
    }

    private sealed record ReadyRule(QueryRuleDefinition Rule, CompiledSql Compiled,
        IReadOnlyList<QueryParameter> Parameters, string Fingerprint, HashSet<string> Dependencies);
}
