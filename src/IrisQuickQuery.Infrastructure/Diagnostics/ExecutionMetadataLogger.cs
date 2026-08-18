using System.Text.Json;
using IrisQuickQuery.Core.Services;
using IrisQuickQuery.Infrastructure.Storage;

namespace IrisQuickQuery.Infrastructure.Diagnostics;

public sealed class ExecutionMetadataLogger : IExecutionMetadataSink, IQueryRunMetadataSink
{
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ExecutionMetadataLogger(AppDataPaths paths) => _paths = paths;

    public async Task WriteAsync(ExecutionMetadata metadata, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.LogDirectory, $"execution-{metadata.Timestamp:yyyy-MM-dd}.jsonl");
        await AppendAsync(path, metadata, cancellationToken);
    }

    public async Task WriteRunAsync(QueryRunMetadata metadata, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.LogDirectory, $"run-{metadata.Timestamp:yyyy-MM-dd}.jsonl");
        await AppendAsync(path, metadata, cancellationToken);
    }

    private async Task AppendAsync<T>(string path, T metadata, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(metadata) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken);
        try { await File.AppendAllTextAsync(path, line, cancellationToken); }
        finally { _gate.Release(); }
    }
}
