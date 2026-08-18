using System.Security.Cryptography;
using System.Text;
using IrisQuickQuery.Infrastructure.Storage;

namespace IrisQuickQuery.Infrastructure.Security;

public sealed class RuleTestValueStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IrisQuickQuery/v1/rule-test-values");
    private readonly SqliteConfigurationRepository _repository;

    public RuleTestValueStore(SqliteConfigurationRepository repository) => _repository = repository;

    public async Task SaveAsync(Guid ruleId, string? values, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(values))
        {
            await _repository.SetSettingAsync(GetSettingKey(ruleId), string.Empty, cancellationToken);
            return;
        }

        var plain = Encoding.UTF8.GetBytes(values);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await _repository.SetSettingAsync(GetSettingKey(ruleId), Convert.ToBase64String(encrypted), cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<string?> GetAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var encoded = await _repository.GetSettingAsync(GetSettingKey(ruleId), cancellationToken);
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        var encrypted = Convert.FromBase64String(encoded);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static string GetSettingKey(Guid ruleId) => $"rule.testValues.dpapi.{ruleId:D}";
}
