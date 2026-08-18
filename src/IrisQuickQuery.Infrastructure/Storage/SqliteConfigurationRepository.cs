using System.Text.Json;
using IrisQuickQuery.Core.Models;
using Microsoft.Data.Sqlite;

namespace IrisQuickQuery.Infrastructure.Storage;

public sealed class SqliteConfigurationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly AppDataPaths _paths;
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _paths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString();

    public SqliteConfigurationRepository(AppDataPaths paths) => _paths = paths;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS snapshots (
                id TEXT PRIMARY KEY,
                version INTEGER NOT NULL,
                name TEXT NOT NULL,
                is_active INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS current_configuration (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                updated_at TEXT NOT NULL,
                json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ConfigurationSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM current_configuration WHERE singleton=1";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<ConfigurationSnapshot>(json, JsonOptions);
    }

    public async Task<ConfigurationSnapshot?> GetLegacyConfigurationForMigrationAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM snapshots ORDER BY version DESC, created_at DESC, is_active DESC LIMIT 1";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<ConfigurationSnapshot>(json, JsonOptions);
    }

    public async Task ClearLegacyConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ConfigurationSnapshot> SaveCurrentAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        snapshot.Id = snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id;
        snapshot.CreatedAt = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO current_configuration(singleton,updated_at,json)
            VALUES(1,$updated,$json)
            ON CONFLICT(singleton) DO UPDATE SET updated_at=$updated,json=$json
            """;
        command.Parameters.AddWithValue("$updated", snapshot.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return snapshot;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
        command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task BackupAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.DatabasePath)) return;
        var target = Path.Combine(_paths.BackupDirectory, $"config-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        await using var source = new SqliteConnection(ConnectionString);
        await source.OpenAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = target,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }
}
