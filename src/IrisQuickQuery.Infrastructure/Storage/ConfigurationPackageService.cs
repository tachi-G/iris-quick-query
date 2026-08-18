using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;

namespace IrisQuickQuery.Infrastructure.Storage;

public sealed record RuleElementPackageManifest(int FormatVersion, int RuleSchemaVersion, DateTimeOffset CreatedAt,
    string MinAppVersion, int RuleCount, int ElementCount, string ContentType,
    string RulesSha256, string ElementsSha256);

internal sealed record RulePackageManifestV2(int FormatVersion, int RuleSchemaVersion, DateTimeOffset CreatedAt,
    string MinAppVersion, int RuleCount, string ContentType, string RulesSha256);

internal sealed record LegacyConfigurationPackageManifest(int FormatVersion, int SchemaVersion, Guid SnapshotId,
    DateTimeOffset CreatedAt, string MinAppVersion, string ConfigurationSha256);

public sealed record ImportedRulePackage(
    IReadOnlyList<QueryRuleDefinition> Rules,
    IReadOnlyList<ElementDefinition> Elements,
    bool IncludesElements);

public sealed class ConfigurationPackageService
{
    private const int CurrentFormatVersion = 3;
    private const string RuleElementContentType = "iris-query-rules-with-elements";
    private const string LegacyRuleContentType = "iris-query-rules";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<int> ExportRulesAsync(IReadOnlyCollection<QueryRuleDefinition> rules,
        IReadOnlyCollection<ElementDefinition> elements, string path, CancellationToken cancellationToken = default)
    {
        var ruleCopies = rules.Select(CloneRule).ToArray();
        var referencedKeys = GetReferencedElementKeys(ruleCopies);
        var elementsByKey = elements
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var duplicateKeys = elementsByKey.Where(x => x.Value.Length > 1).Select(x => x.Key).OrderBy(x => x).ToArray();
        if (duplicateKeys.Length > 0)
            throw new InvalidDataException("全局元素名称重复，无法导出：" + string.Join("、", duplicateKeys));

        var missingKeys = referencedKeys.Where(x => !elementsByKey.ContainsKey(x)).OrderBy(x => x).ToArray();
        if (missingKeys.Length > 0)
            throw new InvalidDataException("规则引用了尚未配置的全局元素：" + string.Join("、", missingKeys));

        var elementCopies = referencedKeys
            .Select(key => CloneElement(elementsByKey[key].Single()))
            .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayOrder)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ValidatePackageConfiguration(ruleCopies, elementCopies);

        var ruleBytes = JsonSerializer.SerializeToUtf8Bytes(ruleCopies, JsonOptions);
        var elementBytes = JsonSerializer.SerializeToUtf8Bytes(elementCopies, JsonOptions);
        var manifest = new RuleElementPackageManifest(CurrentFormatVersion, ConfigurationSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow, "1.0.15", ruleCopies.Length, elementCopies.Length, RuleElementContentType,
            ComputeChecksum(ruleBytes), ComputeChecksum(elementBytes));

        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        await WriteEntryAsync(archive, "rules.json", ruleBytes, cancellationToken);
        await WriteEntryAsync(archive, "elements.json", elementBytes, cancellationToken);
        await WriteEntryAsync(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken);
        return elementCopies.Length;
    }

    public async Task<ImportedRulePackage> ImportRulesAsync(string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifestBytes = await ReadEntryAsync(archive, "manifest.json", cancellationToken);
        using var manifestDocument = JsonDocument.Parse(manifestBytes);
        if (!manifestDocument.RootElement.TryGetProperty("formatVersion", out var versionNode))
            throw new InvalidDataException("规则包 manifest 缺少格式版本。");

        return versionNode.GetInt32() switch
        {
            CurrentFormatVersion => await ImportCurrentPackageAsync(archive, manifestBytes, cancellationToken),
            2 => await ImportV2RulesAsync(archive, manifestBytes, cancellationToken),
            1 => await ImportLegacyRulesAsync(archive, manifestBytes, cancellationToken),
            _ => throw new NotSupportedException("规则包版本高于当前应用支持范围。")
        };
    }

    private static async Task<ImportedRulePackage> ImportCurrentPackageAsync(ZipArchive archive,
        byte[] manifestBytes, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<RuleElementPackageManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("规则包 manifest 无效。");
        if (manifest.RuleSchemaVersion > ConfigurationSnapshot.CurrentSchemaVersion
            || !string.Equals(manifest.ContentType, RuleElementContentType, StringComparison.Ordinal))
            throw new NotSupportedException("规则包内容或版本不受支持。");

        var ruleBytes = await ReadEntryAsync(archive, "rules.json", cancellationToken);
        var elementBytes = await ReadEntryAsync(archive, "elements.json", cancellationToken);
        VerifyChecksum(ruleBytes, manifest.RulesSha256);
        VerifyChecksum(elementBytes, manifest.ElementsSha256);
        var rules = JsonSerializer.Deserialize<List<QueryRuleDefinition>>(ruleBytes, JsonOptions)
            ?? throw new InvalidDataException("规则内容无效。");
        var elements = JsonSerializer.Deserialize<List<ElementDefinition>>(elementBytes, JsonOptions)
            ?? throw new InvalidDataException("全局元素内容无效。");
        if (rules.Count != manifest.RuleCount || elements.Count != manifest.ElementCount)
            throw new InvalidDataException("规则包数量与清单不一致。");

        var preparedElements = PrepareImportedElements(elements);
        var preparedRules = PrepareImportedRules(rules);
        ValidatePackageConfiguration(preparedRules, preparedElements);
        var referencedKeys = GetReferencedElementKeys(preparedRules);
        var packagedKeys = preparedElements.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = referencedKeys.Where(x => !packagedKeys.Contains(x)).OrderBy(x => x).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("规则包缺少规则依赖的全局元素：" + string.Join("、", missing));
        var unused = packagedKeys.Where(x => !referencedKeys.Contains(x)).OrderBy(x => x).ToArray();
        if (unused.Length > 0)
            throw new InvalidDataException("规则包包含未被任何规则使用的全局元素：" + string.Join("、", unused));

        return new ImportedRulePackage(preparedRules, preparedElements, true);
    }

    private static async Task<ImportedRulePackage> ImportV2RulesAsync(ZipArchive archive,
        byte[] manifestBytes, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<RulePackageManifestV2>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("规则包 manifest 无效。");
        if (manifest.RuleSchemaVersion > ConfigurationSnapshot.CurrentSchemaVersion
            || !string.Equals(manifest.ContentType, LegacyRuleContentType, StringComparison.Ordinal))
            throw new NotSupportedException("规则包内容或版本不受支持。");

        var ruleBytes = await ReadEntryAsync(archive, "rules.json", cancellationToken);
        VerifyChecksum(ruleBytes, manifest.RulesSha256);
        var rules = JsonSerializer.Deserialize<List<QueryRuleDefinition>>(ruleBytes, JsonOptions)
            ?? throw new InvalidDataException("规则内容无效。");
        if (rules.Count != manifest.RuleCount) throw new InvalidDataException("规则包数量与清单不一致。");
        return new ImportedRulePackage(PrepareImportedRules(rules), [], false);
    }

    private static async Task<ImportedRulePackage> ImportLegacyRulesAsync(ZipArchive archive,
        byte[] manifestBytes, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<LegacyConfigurationPackageManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("旧配置包 manifest 无效。");
        if (manifest.SchemaVersion > ConfigurationSnapshot.CurrentSchemaVersion)
            throw new NotSupportedException("旧配置包版本高于当前应用支持范围。");

        var configBytes = await ReadEntryAsync(archive, "config.json", cancellationToken);
        VerifyChecksum(configBytes, manifest.ConfigurationSha256);
        var snapshot = JsonSerializer.Deserialize<ConfigurationSnapshot>(configBytes, JsonOptions)
            ?? throw new InvalidDataException("旧配置内容无效。");
        return new ImportedRulePackage(PrepareImportedRules(snapshot.Rules), [], false);
    }

    private static HashSet<string> GetReferencedElementKeys(IEnumerable<QueryRuleDefinition> rules)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            CompiledSql compiled;
            try { compiled = SqlTemplateCompiler.Compile(rule.SqlTemplate ?? string.Empty); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new InvalidDataException($"规则“{rule.Name}”的 SQL 无法解析：{ex.Message}", ex);
            }
            foreach (var key in compiled.ParameterKeys.Where(x => !string.IsNullOrWhiteSpace(x))) keys.Add(key);
            foreach (var key in (rule.OutputMappings ?? []).Select(x => x.ElementKey).Where(x => !string.IsNullOrWhiteSpace(x))) keys.Add(key);
        }
        return keys;
    }

    private static IReadOnlyList<QueryRuleDefinition> PrepareImportedRules(IEnumerable<QueryRuleDefinition> source)
    {
        var order = 10;
        var result = new List<QueryRuleDefinition>();
        foreach (var item in source)
        {
            var copy = CloneRule(item);
            copy.Id = Guid.NewGuid();
            copy.Name ??= string.Empty;
            copy.SqlTemplate ??= string.Empty;
            copy.OutputMappings ??= [];
            foreach (var mapping in copy.OutputMappings)
            {
                mapping.ColumnName ??= string.Empty;
                mapping.ElementKey ??= string.Empty;
            }
            copy.DisplayOrder = order;
            order += 10;
            result.Add(copy);
        }
        return result;
    }

    private static IReadOnlyList<ElementDefinition> PrepareImportedElements(IEnumerable<ElementDefinition> source)
    {
        var result = source.Select(CloneElement).ToArray();
        foreach (var element in result)
        {
            if (element.Id == Guid.Empty) element.Id = Guid.NewGuid();
            element.Key ??= string.Empty;
            element.Label ??= string.Empty;
            element.Group ??= "基本信息";
        }
        var duplicateIds = result.GroupBy(x => x.Id).Where(x => x.Count() > 1).Select(x => x.Key.ToString()).ToArray();
        var duplicateKeys = result.Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        if (duplicateIds.Length > 0 || duplicateKeys.Length > 0)
            throw new InvalidDataException("规则包包含重复的全局元素身份或名称。");
        return result;
    }

    private static void ValidatePackageConfiguration(IReadOnlyCollection<QueryRuleDefinition> rules,
        IReadOnlyCollection<ElementDefinition> elements)
    {
        var snapshot = new ConfigurationSnapshot { Elements = elements.Select(CloneElement).ToList(), Rules = rules.Select(CloneRule).ToList() };
        var errors = ConfigurationValidator.Validate(snapshot).Where(x => !x.IsWarning).Select(x => x.Message).ToArray();
        if (errors.Length > 0) throw new InvalidDataException("规则包配置无效：" + string.Join("；", errors.Take(8)));
    }

    private static QueryRuleDefinition CloneRule(QueryRuleDefinition value)
        => JsonSerializer.Deserialize<QueryRuleDefinition>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    private static ElementDefinition CloneElement(ElementDefinition value)
        => JsonSerializer.Deserialize<ElementDefinition>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    private static string ComputeChecksum(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static void VerifyChecksum(byte[] content, string expected)
    {
        var actual = ComputeChecksum(content);
        if (string.IsNullOrWhiteSpace(expected) || actual.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
            throw new InvalidDataException("规则包校验和不匹配，文件可能已损坏。");
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] bytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var target = entry.Open();
        await target.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string name,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"规则包缺少 {name}。");
        await using var source = entry.Open();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }
}
