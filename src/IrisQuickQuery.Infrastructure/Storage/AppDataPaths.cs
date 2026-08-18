namespace IrisQuickQuery.Infrastructure.Storage;

public sealed class AppDataPaths
{
    public string RootDirectory { get; }
    public string DatabasePath => Path.Combine(RootDirectory, "config.db");
    public string LogDirectory => Path.Combine(RootDirectory, "Logs");
    public string BackupDirectory => Path.Combine(RootDirectory, "Backups");

    public AppDataPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisQuickQuery");
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }
}
