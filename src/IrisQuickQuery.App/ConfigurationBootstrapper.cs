using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Infrastructure.Storage;

namespace IrisQuickQuery.App;

public static class ConfigurationBootstrapper
{
    private const string LastVersionSettingKey = "app.lastVersion";

    public static async Task PrepareAsync(
        SqliteConfigurationRepository repository,
        ConfigurationSnapshot factoryDefault,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var previousVersion = await repository.GetSettingAsync(LastVersionSettingKey, cancellationToken);
        var isUpgrade = !string.IsNullOrWhiteSpace(previousVersion)
                        && !string.Equals(previousVersion, currentVersion, StringComparison.Ordinal);
        if (isUpgrade) await repository.BackupAsync(cancellationToken);

        var current = await repository.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            // 旧版本的最新保存内容（包括尚未启用的草稿）迁移为唯一当前配置，避免丢掉用户最后一次编辑。
            var legacy = await repository.GetLegacyConfigurationForMigrationAsync(cancellationToken);
            if (legacy is not null && !isUpgrade) await repository.BackupAsync(cancellationToken);
            current = legacy ?? factoryDefault;
            await repository.SaveCurrentAsync(current, cancellationToken);
            if (legacy is not null) await repository.ClearLegacyConfigurationsAsync(cancellationToken);
        }
        else if (await repository.GetLegacyConfigurationForMigrationAsync(cancellationToken) is not null)
        {
            // 当前配置已经成功存在时，旧表只剩冗余历史；先备份再清理，避免继续保留版本差异。
            if (!isUpgrade) await repository.BackupAsync(cancellationToken);
            await repository.ClearLegacyConfigurationsAsync(cancellationToken);
        }

        await repository.SetSettingAsync(LastVersionSettingKey, currentVersion, cancellationToken);
    }
}
