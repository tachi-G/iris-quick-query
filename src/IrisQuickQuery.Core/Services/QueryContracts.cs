using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public sealed record QueryParameter(string ElementKey, ElementDataType DataType, object Value);

public sealed record QueryExecutionRequest(
    QueryRuleDefinition Rule,
    string CommandText,
    IReadOnlyList<QueryParameter> Parameters,
    int TimeoutSeconds,
    int MaxRows);

public sealed record QueryExecutionResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool WasTruncated = false,
    IReadOnlyList<string>? ResultColumns = null)
{
    public IReadOnlyList<string> Columns => ResultColumns
        ?? Rows.FirstOrDefault()?.Keys.ToArray()
        ?? [];
}

public interface IDatabaseQueryExecutor
{
    Task<QueryExecutionResult> ExecuteAsync(QueryExecutionRequest request, CancellationToken cancellationToken);
}

public sealed record ExecutionMetadata(
    DateTimeOffset Timestamp,
    Guid RunId,
    Guid RuleId,
    RuleExecutionStatus Status,
    long DurationMilliseconds,
    int RowCount,
    string? ErrorCategory);

public sealed record RuleReadinessMetadata(
    Guid RuleId,
    string RuleName,
    string State,
    IReadOnlyList<string> InputElementKeys,
    IReadOnlyList<string> UnavailableElementKeys);

public sealed record QueryRunMetadata(
    DateTimeOffset Timestamp,
    Guid RunId,
    Guid SnapshotId,
    QueryRunStatus Status,
    int EnabledRuleCount,
    IReadOnlyList<string> ManualElementKeys,
    int ExecutionCount,
    int SuccessCount,
    int NoResultCount,
    int FailedCount,
    int BlockedRuleCount,
    int ConflictCount,
    IReadOnlyList<RuleReadinessMetadata> Rules);

public interface IExecutionMetadataSink
{
    Task WriteAsync(ExecutionMetadata metadata, CancellationToken cancellationToken);
}

public interface IQueryRunMetadataSink
{
    Task WriteRunAsync(QueryRunMetadata metadata, CancellationToken cancellationToken);
}

public sealed class NullExecutionMetadataSink : IExecutionMetadataSink
{
    public Task WriteAsync(ExecutionMetadata metadata, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NullQueryRunMetadataSink : IQueryRunMetadataSink
{
    public Task WriteRunAsync(QueryRunMetadata metadata, CancellationToken cancellationToken) => Task.CompletedTask;
}
