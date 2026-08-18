using System.Text.Json;
using IrisQuickQuery.Infrastructure.Storage;

namespace IrisQuickQuery.Infrastructure.Database;

public enum TlsConfigurationState { NotConfigured, ConfiguredUnverified }

public sealed class ConnectionProfile
{
    public string DriverName { get; set; } = "InterSystems IRIS ODBC35";
    public string? Dsn { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1972;
    public string Namespace { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? TlsConfigurationName { get; set; }
    public bool SavePassword { get; set; } = true;
    public TlsConfigurationState TlsState => string.IsNullOrWhiteSpace(TlsConfigurationName)
        ? TlsConfigurationState.NotConfigured : TlsConfigurationState.ConfiguredUnverified;
}

public sealed class ConnectionProfileRepository
{
    private const string SettingKey = "connection.profile";
    private readonly SqliteConfigurationRepository _repository;
    public ConnectionProfileRepository(SqliteConfigurationRepository repository) => _repository = repository;

    public async Task<ConnectionProfile> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await _repository.GetSettingAsync(SettingKey, cancellationToken);
        return string.IsNullOrWhiteSpace(json) ? new ConnectionProfile() : JsonSerializer.Deserialize<ConnectionProfile>(json) ?? new ConnectionProfile();
    }

    public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        => _repository.SetSettingAsync(SettingKey, JsonSerializer.Serialize(profile), cancellationToken);
}
