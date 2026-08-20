using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;
using IrisQuickQuery.Infrastructure.Security;

namespace IrisQuickQuery.Infrastructure.Database;

public sealed class OdbcQueryExecutor : IDatabaseQueryExecutor
{
    private readonly ConnectionProfileRepository _profiles;
    private readonly DpapiCredentialStore _credentials;

    public OdbcQueryExecutor(ConnectionProfileRepository profiles, DpapiCredentialStore credentials)
    {
        _profiles = profiles;
        _credentials = credentials;
    }

    public async Task<QueryExecutionResult> ExecuteAsync(QueryExecutionRequest request, CancellationToken cancellationToken)
    {
        var unsafeIssue = SqlSafetyValidator.Validate(request.CommandText).FirstOrDefault(x => !x.IsWarning);
        if (unsafeIssue is not null) throw new InvalidOperationException("ODBC 执行已阻止非只读 SQL：" + unsafeIssue.Message);
        var profile = await _profiles.GetAsync(cancellationToken);
        var password = await _credentials.GetAsync(cancellationToken) ?? string.Empty;
        return await Task.Run(
            () => ExecuteOnWorkerAsync(request, profile, password, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<QueryExecutionResult> ExecuteOnWorkerAsync(QueryExecutionRequest request,
        ConnectionProfile profile, string password, CancellationToken cancellationToken)
    {
        var connectionString = BuildConnectionString(profile, password);
        await using var connection = await OpenWithSingleRetryAsync(
            token => OpenConnectionAsync(connectionString, token), cancellationToken).ConfigureAwait(false);
        return await ExecuteCommandAsync(connection, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OdbcConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new OdbcConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<T> OpenWithSingleRetryAsync<T>(Func<CancellationToken, Task<T>> openAsync,
        CancellationToken cancellationToken, TimeSpan? retryDelay = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await openAsync(cancellationToken).ConfigureAwait(false); }
            catch (DbException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(retryDelay ?? TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<QueryExecutionResult> ExecuteCommandAsync(OdbcConnection connection,
        QueryExecutionRequest request, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = request.CommandText;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = request.TimeoutSeconds;
        foreach (var parameter in request.Parameters)
            command.Parameters.Add(CreateParameter(parameter));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var registration = timeout.Token.Register(() => { try { command.Cancel(); } catch { } });
        try
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new InvalidOperationException("查询结果包含重复列名，请在 SQL 中使用唯一别名。");
            while (rows.Count <= request.MaxRows && await reader.ReadAsync(timeout.Token))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < names.Length; i++) row[names[i]] = await reader.IsDBNullAsync(i, timeout.Token) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            var truncated = rows.Count > request.MaxRows;
            if (truncated) rows.RemoveAt(rows.Count - 1);
            return new QueryExecutionResult(rows, truncated, names);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"SQL 执行超过 {request.TimeoutSeconds} 秒。\n");
        }
    }

    public async Task TestConnectionAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken = default)
    {
        await Task.Run(async () =>
        {
            await using var connection = new OdbcConnection(BuildConnectionString(profile, password));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public static string BuildConnectionString(ConnectionProfile profile, string password)
    {
        var builder = new OdbcConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(profile.Dsn)) builder["DSN"] = profile.Dsn;
        else
        {
            builder["Driver"] = "{" + profile.DriverName.Trim('{', '}') + "}";
            builder["Server"] = profile.Host;
            builder["Port"] = profile.Port;
            builder["Database"] = profile.Namespace;
        }
        builder["UID"] = profile.Username;
        builder["PWD"] = password;
        if (!string.IsNullOrWhiteSpace(profile.TlsConfigurationName)) builder["SSLServerName"] = profile.TlsConfigurationName;
        return builder.ConnectionString;
    }

    private static OdbcParameter CreateParameter(QueryParameter parameter)
    {
        var type = parameter.DataType switch
        {
            ElementDataType.Text => OdbcType.NVarChar,
            ElementDataType.Int64 => OdbcType.BigInt,
            ElementDataType.Decimal => OdbcType.Decimal,
            ElementDataType.Date => OdbcType.Date,
            ElementDataType.DateTime => OdbcType.DateTime,
            ElementDataType.Boolean => OdbcType.Bit,
            _ => OdbcType.NVarChar
        };
        var value = parameter.Value is DateOnly date ? date.ToDateTime(TimeOnly.MinValue) : parameter.Value;
        return new OdbcParameter { OdbcType = type, Value = value };
    }
}
