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
}
