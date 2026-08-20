using System.Data.Common;
using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;
using IrisQuickQuery.Infrastructure.Database;

namespace IrisQuickQuery.Core.Tests;

public sealed class OdbcBoundaryTests
{
    [Fact]
    public void DsnlessConnectionString_ContainsNoUnexpectedPlaintextFields()
    {
        var profile = new ConnectionProfile { DriverName = "InterSystems IRIS ODBC35", Host = "127.0.0.1", Port = 1972, Namespace = "TEST", Username = "readonly" };
        var text = OdbcQueryExecutor.BuildConnectionString(profile, "secret");
        Assert.Contains("InterSystems IRIS ODBC35", text);
        Assert.Contains("127.0.0.1", text);
        Assert.Contains("TEST", text);
        Assert.Contains("readonly", text);
        Assert.Contains("secret", text);
    }

    [Fact]
    public void ConnectionString_PreservesOptionalIrisTlsServerName()
    {
        var profile = new ConnectionProfile
        {
            DriverName = "InterSystems IRIS ODBC35",
            Host = "127.0.0.1",
            Port = 1972,
            Namespace = "TEST",
            Username = "readonly",
            TlsConfigurationName = "HospitalTls"
        };

        var text = OdbcQueryExecutor.BuildConnectionString(profile, "secret");

        Assert.Contains("SSLServerName=HospitalTls", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirstTransientOpenFailure_IsRetriedExactlyOnce()
    {
        var attempts = 0;

        var value = await OdbcQueryExecutor.OpenWithSingleRetryAsync<int>(_ =>
        {
            attempts++;
            if (attempts == 1) throw new TestDbException("cold start");
            return Task.FromResult(42);
        }, CancellationToken.None, TimeSpan.Zero);

        Assert.Equal(42, value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecutorRejectsWriteSqlBeforeReadingConnectionSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "IrisQuickQueryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new IrisQuickQuery.Infrastructure.Storage.AppDataPaths(root);
            var repository = new IrisQuickQuery.Infrastructure.Storage.SqliteConfigurationRepository(paths);
            await repository.InitializeAsync();
            var executor = new OdbcQueryExecutor(new ConnectionProfileRepository(repository), new IrisQuickQuery.Infrastructure.Security.DpapiCredentialStore(repository));
            var rule = new QueryRuleDefinition { Name = "bad", SqlTemplate = "DELETE FROM T" };
            var request = new QueryExecutionRequest(rule, "DELETE FROM T", [], 1, 1);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(request, CancellationToken.None));
            Assert.Contains("非只读", error.Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class TestDbException(string message) : DbException(message);
}
