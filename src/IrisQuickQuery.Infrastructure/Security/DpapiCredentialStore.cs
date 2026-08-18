using System.Security.Cryptography;
using System.Text;
using IrisQuickQuery.Infrastructure.Storage;

namespace IrisQuickQuery.Infrastructure.Security;

public sealed class DpapiCredentialStore
{
    private const string SettingKey = "connection.password.dpapi";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IrisQuickQuery/v1/connection-password");
    private readonly SqliteConfigurationRepository _repository;

    public DpapiCredentialStore(SqliteConfigurationRepository repository) => _repository = repository;

    public async Task SaveAsync(string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password)) { await _repository.SetSettingAsync(SettingKey, string.Empty, cancellationToken); return; }
        var plain = Encoding.UTF8.GetBytes(password);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await _repository.SetSettingAsync(SettingKey, Convert.ToBase64String(encrypted), cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        var encoded = await _repository.GetSettingAsync(SettingKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        var encrypted = Convert.FromBase64String(encoded);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
