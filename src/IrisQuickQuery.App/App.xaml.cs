using System.Windows;
using IrisQuickQuery.App.ViewModels;
using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;
using IrisQuickQuery.Infrastructure.Database;
using IrisQuickQuery.Infrastructure.Diagnostics;
using IrisQuickQuery.Infrastructure.Security;
using IrisQuickQuery.Infrastructure.Storage;

namespace IrisQuickQuery.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var paths = new AppDataPaths();
            var repository = new SqliteConfigurationRepository(paths);
            await repository.InitializeAsync();
            var currentVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            await ConfigurationBootstrapper.PrepareAsync(repository, CreateEmptySnapshot(), currentVersion);
            var profileRepository = new ConnectionProfileRepository(repository);
            var credentials = new DpapiCredentialStore(repository);
            var executor = new OdbcQueryExecutor(profileRepository, credentials);
            var metadataLogger = new ExecutionMetadataLogger(paths);
            var scheduler = new RuleScheduler(executor, metadataLogger);
            var packages = new ConfigurationPackageService();
            var services = new ApplicationServices(paths, repository, profileRepository, credentials, executor, scheduler, metadataLogger, packages);
            AsyncRelayCommand.ExecutionFailed += (_, args) =>
                MessageBox.Show($"操作失败：{args.Exception.Message}", "IRIS 快捷查询", MessageBoxButton.OK, MessageBoxImage.Error);
            var window = new MainWindow { DataContext = new MainViewModel(services) };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"应用初始化失败：{ex.Message}", "IRIS 快捷查询", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static ConfigurationSnapshot CreateEmptySnapshot() => new() { Name = "空白配置" };

}

public sealed record ApplicationServices(
    AppDataPaths Paths,
    SqliteConfigurationRepository Configurations,
    ConnectionProfileRepository Profiles,
    DpapiCredentialStore Credentials,
    OdbcQueryExecutor QueryExecutor,
    RuleScheduler Scheduler,
    IQueryRunMetadataSink RunDiagnostics,
    ConfigurationPackageService Packages);
