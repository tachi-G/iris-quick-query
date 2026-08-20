using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;
using IrisQuickQuery.Infrastructure.Diagnostics;
using IrisQuickQuery.Infrastructure.Database;
using IrisQuickQuery.Infrastructure.Security;
using IrisQuickQuery.App;
using IrisQuickQuery.App.ViewModels;
using IrisQuickQuery.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace IrisQuickQuery.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "IrisQuickQueryTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Repository_SaveCurrentOverwritesTheSingleConfiguration()
    {
        var repository = new SqliteConfigurationRepository(new AppDataPaths(_temp)); await repository.InitializeAsync();
        var current = TestConfig.CreateElements("a"); current.Name = "first";
        await repository.SaveCurrentAsync(current);
        current.Name = "second";
        current.Elements.Single().IsDate = true;
        await repository.SaveCurrentAsync(current);

        var reopened = await repository.GetCurrentAsync();
        Assert.NotNull(reopened);
        Assert.Equal("second", reopened.Name);
        Assert.True(reopened.Elements.Single().IsDate);
    }

    [Fact]
    public async Task RulePackage_RoundTripsRulesAndReferencedElements_WithChecksumsAndNewRuleIdentity()
    {
        Directory.CreateDirectory(_temp);
        var service = new ConfigurationPackageService();
        var patientId = new ElementDefinition
            { Key = "patient_id", Label = "患者编号", Group = "患者", DisplayOrder = 10, IsSensitive = true };
        var visitNo = new ElementDefinition
            { Key = "visit_no", Label = "就诊号", Group = "就诊", DisplayOrder = 20, CanInput = false };
        var unused = new ElementDefinition
            { Key = "unused", Label = "未使用元素", Group = "其他", DisplayOrder = 30 };
        var source = new QueryRuleDefinition
        {
            Name = "按患者号查询",
            SqlTemplate = "SELECT visit_no FROM visits WHERE patient_id={{patient_id}}",
            OutputMappings = [new() { ColumnName = "visit_no", ElementKey = "visit_no" }]
        };
        var path = Path.Combine(_temp, "shared.irisqconfig");
        var exportedElementCount = await service.ExportRulesAsync([source], [patientId, visitNo, unused], path);
        var imported = await service.ImportRulesAsync(path);

        Assert.Equal(2, exportedElementCount);
        Assert.True(imported.IncludesElements);
        Assert.Single(imported.Rules);
        Assert.NotEqual(source.Id, imported.Rules.Single().Id);
        Assert.Equal(SqlTemplateCompiler.Compile(source.SqlTemplate).CommandText.Replace("\r", string.Empty).Replace("\n", " ").Replace("  ", " "),
            SqlTemplateCompiler.Compile(imported.Rules.Single().SqlTemplate).CommandText.Replace("\r", string.Empty).Replace("\n", " ").Replace("  ", " "));
        Assert.Single(imported.QueryObjects);
        Assert.Equal(["patient_id", "visit_no"], imported.Elements.Select(x => x.Key).Order());
        Assert.Equal(patientId.Id, imported.Elements.Single(x => x.Key == "patient_id").Id);
        Assert.True(imported.Elements.Single(x => x.Key == "patient_id").IsSensitive);
        Assert.False(imported.Elements.Single(x => x.Key == "visit_no").CanInput);
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.Contains(archive.Entries, x => x.FullName == "rules.json");
        Assert.Contains(archive.Entries, x => x.FullName == "query-objects.json");
        Assert.Contains(archive.Entries, x => x.FullName == "elements.json");
        Assert.Contains(archive.Entries, x => x.FullName == "manifest.json");
        Assert.DoesNotContain(archive.Entries, x => x.FullName == "config.json");
    }

    [Fact]
    public async Task QueryObjectPackage_PreservesSharedSqlEntrySettingsAndObjectNames()
    {
        Directory.CreateDirectory(_temp);
        var service = new ConfigurationPackageService();
        var patientId = new ElementDefinition { Key = "patient_id", Label = "患者编号" };
        var visitNo = new ElementDefinition { Key = "visit_no", Label = "就诊号" };
        var queryObject = new QueryObjectDefinition
        {
            Name = "患者就诊关系",
            BaseSqlTemplate = "SELECT visit_no, patient_id FROM visits",
            OutputMappings =
            [
                new OutputMapping { ColumnName = "visit_no", ElementKey = "visit_no" },
                new OutputMapping { ColumnName = "patient_id", ElementKey = "patient_id" }
            ],
            Entries =
            [
                new QueryEntryDefinition
                {
                    Name = "按患者取最近就诊",
                    FilterTemplate = "patient_id={{patient_id}}",
                    OrderByTemplate = "created_at DESC",
                    TakeFirst = true
                }
            ]
        };
        var path = Path.Combine(_temp, "objects.irisqconfig");

        await service.ExportQueryObjectsAsync([queryObject], [patientId, visitNo], path);
        var imported = await service.ImportRulesAsync(path);

        var importedObject = Assert.Single(imported.QueryObjects);
        var importedEntry = Assert.Single(importedObject.Entries);
        Assert.Equal("患者就诊关系", importedObject.Name);
        Assert.Equal(queryObject.BaseSqlTemplate, importedObject.BaseSqlTemplate);
        Assert.Equal("按患者取最近就诊", importedEntry.Name);
        Assert.Equal("patient_id={{patient_id}}", importedEntry.FilterTemplate);
        Assert.Equal("created_at DESC", importedEntry.OrderByTemplate);
        Assert.True(importedEntry.TakeFirst);
        Assert.Contains("SELECT TOP 1", Assert.Single(imported.Rules).SqlTemplate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RulePackage_RejectsExportWhenReferencedElementIsMissing()
    {
        Directory.CreateDirectory(_temp);
        var service = new ConfigurationPackageService();
        var source = new QueryRuleDefinition
        {
            Name = "缺少元素",
            SqlTemplate = "SELECT visit_no FROM visits WHERE patient_id={{patient_id}}",
            OutputMappings = [new() { ColumnName = "visit_no", ElementKey = "visit_no" }]
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ExportRulesAsync([source], [new ElementDefinition { Key = "patient_id", Label = "患者编号" }],
                Path.Combine(_temp, "invalid.irisqconfig")));

        Assert.Contains("visit_no", error.Message);
    }

    [Fact]
    public async Task RulePackage_StillImportsVersion2RulesWithoutChangingElements()
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "version2.irisqconfig");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var source = new QueryRuleDefinition
        {
            Name = "旧版规则",
            SqlTemplate = "SELECT visit_no FROM visits WHERE patient_id={{patient_id}}",
            OutputMappings = [new() { ColumnName = "visit_no", ElementKey = "visit_no" }]
        };
        var ruleBytes = JsonSerializer.SerializeToUtf8Bytes(new[] { source }, options);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 2,
            ruleSchemaVersion = ConfigurationSnapshot.CurrentSchemaVersion,
            createdAt = DateTimeOffset.UtcNow,
            minAppVersion = "1.0.4",
            ruleCount = 1,
            contentType = "iris-query-rules",
            rulesSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ruleBytes))
        }, options);
        await using (var stream = File.Create(path))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var entryContent in new[] { (Name: "rules.json", Bytes: ruleBytes), (Name: "manifest.json", Bytes: manifestBytes) })
            {
                var entry = archive.CreateEntry(entryContent.Name);
                await using var target = entry.Open();
                await target.WriteAsync(entryContent.Bytes);
            }
        }

        var imported = await new ConfigurationPackageService().ImportRulesAsync(path);

        Assert.False(imported.IncludesElements);
        Assert.Empty(imported.Elements);
        Assert.Single(imported.Rules);
        Assert.Equal(source.SqlTemplate, imported.Rules.Single().SqlTemplate);
        Assert.NotEqual(source.Id, imported.Rules.Single().Id);
    }

    [Fact]
    public async Task RulePackage_StillImportsVersion3RulesAndElements()
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "version3.irisqconfig");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var source = new QueryRuleDefinition
        {
            Name = "旧版带元素规则",
            SqlTemplate = "SELECT visit_no FROM visits WHERE patient_id={{patient_id}}",
            OutputMappings = [new() { ColumnName = "visit_no", ElementKey = "visit_no" }]
        };
        var elements = new[]
        {
            new ElementDefinition { Key = "patient_id", Label = "患者编号" },
            new ElementDefinition { Key = "visit_no", Label = "就诊号" }
        };
        var ruleBytes = JsonSerializer.SerializeToUtf8Bytes(new[] { source }, options);
        var elementBytes = JsonSerializer.SerializeToUtf8Bytes(elements, options);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 3,
            ruleSchemaVersion = 1,
            createdAt = DateTimeOffset.UtcNow,
            minAppVersion = "1.0.15",
            ruleCount = 1,
            elementCount = 2,
            contentType = "iris-query-rules-with-elements",
            rulesSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ruleBytes)),
            elementsSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(elementBytes))
        }, options);
        await using (var stream = File.Create(path))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var content in new[]
                     {
                         (Name: "rules.json", Bytes: ruleBytes),
                         (Name: "elements.json", Bytes: elementBytes),
                         (Name: "manifest.json", Bytes: manifestBytes)
                     })
            {
                var entry = archive.CreateEntry(content.Name);
                await using var target = entry.Open();
                await target.WriteAsync(content.Bytes);
            }
        }

        var imported = await new ConfigurationPackageService().ImportRulesAsync(path);

        Assert.True(imported.IncludesElements);
        Assert.Empty(imported.QueryObjects);
        Assert.Single(imported.Rules);
        Assert.Equal(2, imported.Elements.Count);
    }

    [Fact]
    public void ElementImportPlanner_MergesByIdentityOrKeyAndKeepsUnrelatedLocalElements()
    {
        var renamedId = Guid.NewGuid();
        var localKeyId = Guid.NewGuid();
        var untouchedId = Guid.NewGuid();
        var addedId = Guid.NewGuid();
        var local = new[]
        {
            new ElementDefinition { Id = renamedId, Key = "old_patient_id", Label = "旧患者号" },
            new ElementDefinition { Id = localKeyId, Key = "visit_no", Label = "本机就诊号" },
            new ElementDefinition { Id = untouchedId, Key = "local_only", Label = "本机保留" }
        };
        var imported = new[]
        {
            new ElementDefinition { Id = renamedId, Key = "patient_id", Label = "患者编号", IsSensitive = true },
            new ElementDefinition { Id = Guid.NewGuid(), Key = "visit_no", Label = "就诊号", CanInput = false },
            new ElementDefinition { Id = addedId, Key = "visit_date", Label = "就诊日期", IsDate = true }
        };

        var plan = ElementImportPlanner.Create(local, imported);

        Assert.True(plan.CanApply);
        Assert.Equal(1, plan.AddedCount);
        Assert.Equal(2, plan.UpdatedCount);
        Assert.Equal(0, plan.ReusedCount);
        Assert.Equal(1, plan.UntouchedLocalCount);
        Assert.Equal(renamedId, plan.MergedElements.Single(x => x.Key == "patient_id").Id);
        Assert.Equal(localKeyId, plan.MergedElements.Single(x => x.Key == "visit_no").Id);
        Assert.False(plan.MergedElements.Single(x => x.Key == "visit_no").CanInput);
        Assert.Equal(addedId, plan.MergedElements.Single(x => x.Key == "visit_date").Id);
        Assert.Contains(plan.MergedElements, x => x.Id == untouchedId && x.Key == "local_only");
    }

    [Fact]
    public void ElementImportPlanner_StopsWhenIdentityAndKeyPointToDifferentLocalElements()
    {
        var firstId = Guid.NewGuid();
        var local = new[]
        {
            new ElementDefinition { Id = firstId, Key = "patient_id", Label = "患者编号" },
            new ElementDefinition { Id = Guid.NewGuid(), Key = "visit_no", Label = "就诊号" }
        };
        var imported = new[]
        {
            new ElementDefinition { Id = firstId, Key = "visit_no", Label = "冲突元素" }
        };

        var plan = ElementImportPlanner.Create(local, imported);

        Assert.False(plan.CanApply);
        Assert.Single(plan.Conflicts);
        Assert.Equal(["patient_id", "visit_no"], plan.MergedElements.Select(x => x.Key));
    }

    [Fact]
    public async Task Repository_CreatesConsistentOnlineBackup()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths); await repository.InitializeAsync();
        var source = TestConfig.CreateElements("a");
        await repository.SaveCurrentAsync(source);
        await repository.BackupAsync();
        var backup = Directory.GetFiles(paths.BackupDirectory, "config-*.db").Single();
        Assert.True(new FileInfo(backup).Length > 0);
    }

    [Fact]
    public async Task RunDiagnostics_LogZeroExecutionReasonWithoutPatientValues()
    {
        var paths = new AppDataPaths(_temp);
        var logger = new ExecutionMetadataLogger(paths);
        var metadata = new QueryRunMetadata(
            DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), QueryRunStatus.Completed,
            1, ["id_card_no"], 0, 0, 0, 0, 0, 0,
            [new RuleReadinessMetadata(Guid.NewGuid(), "身份证号查询登记号", "WaitingForInputs", ["id_card_no"], ["id_card_no"])]);

        await logger.WriteRunAsync(metadata, CancellationToken.None);

        var log = await File.ReadAllTextAsync(Directory.GetFiles(paths.LogDirectory, "run-*.jsonl").Single());
        Assert.Contains("WaitingForInputs", log);
        Assert.Contains("id_card_no", log);
        Assert.DoesNotContain("110101199001011234", log);
    }

    [Fact]
    public async Task ConnectionProfileAndDpapiPassword_SurviveRepositoryReopen()
    {
        var paths = new AppDataPaths(_temp);
        var firstRepository = new SqliteConfigurationRepository(paths);
        await firstRepository.InitializeAsync();
        var profiles = new ConnectionProfileRepository(firstRepository);
        var credentials = new DpapiCredentialStore(firstRepository);
        await profiles.SaveAsync(new ConnectionProfile
        {
            Host = "iris.hospital.local", Port = 1972, Namespace = "HOSPITAL", Username = "readonly", SavePassword = true
        });
        await credentials.SaveAsync("test-password");

        var reopenedRepository = new SqliteConfigurationRepository(paths);
        await reopenedRepository.InitializeAsync();
        var reopenedProfile = await new ConnectionProfileRepository(reopenedRepository).GetAsync();
        var reopenedPassword = await new DpapiCredentialStore(reopenedRepository).GetAsync();

        Assert.Equal("iris.hospital.local", reopenedProfile.Host);
        Assert.Equal("readonly", reopenedProfile.Username);
        Assert.Equal("test-password", reopenedPassword);
    }

    [Fact]
    public async Task RuleTestValues_AreEncryptedPerRuleAndSurviveRepositoryReopen()
    {
        var paths = new AppDataPaths(_temp);
        var firstRepository = new SqliteConfigurationRepository(paths);
        await firstRepository.InitializeAsync();
        var ruleId = Guid.NewGuid();
        var store = new RuleTestValueStore(firstRepository);

        await store.SaveAsync(ruleId, "id_card_no=110101199001011234");

        var rawSetting = await firstRepository.GetSettingAsync($"rule.testValues.dpapi.{ruleId:D}");
        Assert.NotNull(rawSetting);
        Assert.DoesNotContain("110101199001011234", rawSetting);
        var reopened = new SqliteConfigurationRepository(paths);
        await reopened.InitializeAsync();
        Assert.Equal("id_card_no=110101199001011234", await new RuleTestValueStore(reopened).GetAsync(ruleId));
    }

    [Fact]
    public async Task RepeatedSaves_UpdateTheSameCurrentConfiguration()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var current = TestConfig.CreateElements("id_card_no");
        await repository.SaveCurrentAsync(current);

        var profiles = new ConnectionProfileRepository(repository);
        var credentials = new DpapiCredentialStore(repository);
        var executor = new OdbcQueryExecutor(profiles, credentials);
        var logger = new ExecutionMetadataLogger(paths);
        var services = new ApplicationServices(paths, repository, profiles, credentials, executor,
            new RuleScheduler(executor, logger), logger, new ConfigurationPackageService());
        var viewModel = new DraftConfigurationViewModel(services);
        await viewModel.LoadCurrentAsync();

        viewModel.Name = "第一次保存";
        await viewModel.SaveAsync();
        viewModel.Name = "第二次保存";
        await viewModel.SaveAsync();

        Assert.Equal("第二次保存", (await repository.GetCurrentAsync())!.Name);
    }

    [Fact]
    public async Task ElementEdits_OverwriteCurrentConfigurationImmediately()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var initial = TestConfig.CreateElements("id_card_no");
        await repository.SaveCurrentAsync(initial);

        var profiles = new ConnectionProfileRepository(repository);
        var credentials = new DpapiCredentialStore(repository);
        var executor = new OdbcQueryExecutor(profiles, credentials);
        var logger = new ExecutionMetadataLogger(paths);
        var services = new ApplicationServices(paths, repository, profiles, credentials, executor,
            new RuleScheduler(executor, logger), logger, new ConfigurationPackageService());
        var viewModel = new DraftConfigurationViewModel(services);
        await viewModel.LoadCurrentAsync();

        viewModel.Name = "医院当前配置";
        await viewModel.SaveAsync();

        var saved = await repository.GetCurrentAsync();
        Assert.NotNull(saved);
        Assert.Equal(initial.Id, saved.Id);
        Assert.Equal("医院当前配置", saved.Name);
    }

    [Fact]
    public async Task ElementDisplayAndDateEdits_SaveAndSurviveReopen()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var current = TestConfig.CreateElements("patient_id", "visit_no");
        current.Rules.Add(TestConfig.Rule("查询就诊号", "patient_id", "visit_no"));
        await repository.SaveCurrentAsync(current);
        var viewModel = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await viewModel.LoadCurrentAsync();

        var edited = viewModel.Elements.Single(x => x.Key == "patient_id");
        edited.Label = "患者编号";
        edited.Group = "患者标识";
        edited.DisplayOrder = 30;
        edited.Width = 360;
        edited.IsDate = true;
        await viewModel.SaveAsync();

        var reopened = new SqliteConfigurationRepository(paths);
        await reopened.InitializeAsync();
        var saved = await reopened.GetCurrentAsync();
        Assert.NotNull(saved);
        Assert.Equal("患者编号", saved.Elements.Single(x => x.Key == "patient_id").Label);
        Assert.Equal(360, saved.Elements.Single(x => x.Key == "patient_id").Width);
        Assert.True(saved.Elements.Single(x => x.Key == "patient_id").IsDate);
        Assert.Equal("配置已保存。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task InvalidElementGridEdits_ShowValidationAndDoNotWriteOrThrow()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var current = TestConfig.CreateElements("patient_id");
        await repository.SaveCurrentAsync(current);
        var viewModel = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await viewModel.LoadCurrentAsync();

        var edited = viewModel.Elements.Single();
        edited.Key = null!;
        edited.Width = 0;
        edited.ValidationPattern = "[";
        var exception = await Record.ExceptionAsync(viewModel.SaveAsync);

        Assert.Null(exception);
        Assert.Contains("key 无效", viewModel.StatusMessage);
        Assert.Equal("patient_id", (await repository.GetCurrentAsync())!.Elements.Single().Key);
    }

    [Fact]
    public async Task ChangingReferencedElementType_SavesWithoutSeparateActivation()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var current = TestConfig.CreateElements("patient_id", "visit_no");
        current.Rules.Add(TestConfig.Rule("查询就诊号", "patient_id", "visit_no"));
        await repository.SaveCurrentAsync(current);
        var viewModel = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await viewModel.LoadCurrentAsync();

        viewModel.Elements.Single(x => x.Key == "patient_id").DataType = ElementDataType.Int64;
        var exception = await Record.ExceptionAsync(viewModel.SaveAsync);

        Assert.Null(exception);
        Assert.Equal(ElementDataType.Int64, (await repository.GetCurrentAsync())!.Elements.Single(x => x.Key == "patient_id").DataType);
        Assert.Equal("配置已保存。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RenamingElementKey_SynchronizesRulesAndKeepsValidatedConfigurationReady()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var current = TestConfig.CreateElements("IDCard", "visit_no");
        current.Rules.Add(TestConfig.Rule("查询就诊号", "IDCard", "visit_no"));
        await repository.SaveCurrentAsync(current);
        var viewModel = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await viewModel.LoadCurrentAsync();

        viewModel.Elements.Single(x => x.Key == "IDCard").Key = "IDCardNumber";
        await viewModel.SaveAsync();

        var saved = await repository.GetCurrentAsync();
        Assert.NotNull(saved);
        Assert.Contains("{{IDCardNumber}}", saved.Rules.Single().SqlTemplate);
        Assert.DoesNotContain("{{IDCard}}", saved.Rules.Single().SqlTemplate);
        Assert.Contains(saved.Rules.Single().OutputMappings, x => x.ElementKey == "visit_no");
        Assert.Equal("配置已保存。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ConfigurationBootstrapper_FreshEnvironmentStartsWithoutElementsOrRules()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();

        await ConfigurationBootstrapper.PrepareAsync(repository,
            new ConfigurationSnapshot { Name = "空白配置" }, "1.0.15");

        var current = Assert.IsType<ConfigurationSnapshot>(await repository.GetCurrentAsync());
        Assert.Equal("空白配置", current.Name);
        Assert.Empty(current.Elements);
        Assert.Empty(current.Rules);
    }

    [Fact]
    public async Task ConfigurationBootstrapper_UpgradeKeepsExistingElementsAndRulesWhenNewDefaultIsEmpty()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var existing = TestConfig.CreateElements("hospital_patient_id", "visit_no");
        existing.Rules.Add(TestConfig.Rule("现有规则", "hospital_patient_id", "visit_no"));
        await repository.SaveCurrentAsync(existing);
        await repository.SetSettingAsync("app.lastVersion", "1.0.14");

        await ConfigurationBootstrapper.PrepareAsync(repository,
            new ConfigurationSnapshot { Name = "空白配置" }, "1.0.15");

        var current = Assert.IsType<ConfigurationSnapshot>(await repository.GetCurrentAsync());
        Assert.Equal(existing.Id, current.Id);
        Assert.Equal(2, current.Elements.Count);
        Assert.Single(current.Rules);
        Assert.Single(current.QueryObjects);
        Assert.NotEmpty(Directory.GetFiles(paths.BackupDirectory, "config-*.db"));
    }

    [Fact]
    public async Task ConfigurationBootstrapper_GroupsCompatibleLegacyRulesAndPreservesRuntimeIds()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var existing = TestConfig.CreateElements("patient_id", "registration_no", "patient_name");
        var first = TestConfig.Rule("按患者号查登记号", "patient_id", "registration_no");
        var second = TestConfig.Rule("按患者号查姓名", "patient_id", "patient_name");
        existing.Rules.AddRange([first, second]);
        await repository.SaveCurrentAsync(existing);
        await repository.SetSettingAsync("app.lastVersion", "1.0.15");

        await ConfigurationBootstrapper.PrepareAsync(repository,
            new ConfigurationSnapshot { Name = "空白配置" }, "1.0.16");

        var current = Assert.IsType<ConfigurationSnapshot>(await repository.GetCurrentAsync());
        var queryObject = Assert.Single(current.QueryObjects);
        Assert.Equal(2, queryObject.Entries.Count);
        Assert.Equal(2, current.Rules.Count);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), current.Rules.Select(x => x.Id).Order());
        Assert.NotEmpty(Directory.GetFiles(paths.BackupDirectory, "config-*.db"));
    }

    [Fact]
    public async Task ConfigurationBootstrapper_MigratesLatestLegacyConfigurationInsteadOfSeedingDefaults()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var configured = TestConfig.CreateElements("hospital_patient_id");
        configured.Name = "医院现有配置";
        configured.Elements.Single().IsDate = true;
        await InsertLegacySnapshotAsync(paths, configured, version: 3, isActive: false);

        await ConfigurationBootstrapper.PrepareAsync(repository, LegacyFactoryDefault(), "1.0.7");

        var current = await repository.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal(configured.Id, current.Id);
        Assert.Contains(current.Elements, x => x.Key == "hospital_patient_id");
        Assert.True(current.Elements.Single(x => x.Key == "hospital_patient_id").IsDate);
        Assert.DoesNotContain(current.Elements, x => x.Key == "id_card_no");
        Assert.Null(await repository.GetLegacyConfigurationForMigrationAsync());
        Assert.NotEmpty(Directory.GetFiles(paths.BackupDirectory, "config-*.db"));
    }

    [Fact]
    public async Task ConfigurationBootstrapper_OnUpgradeMigratesLatestLegacySaveAndCreatesBackup()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var configured = TestConfig.CreateElements("hospital_patient_id");
        configured.Name = "医院现有配置";
        await InsertLegacySnapshotAsync(paths, configured, version: 4, isActive: false);
        var factory = LegacyFactoryDefault();
        await InsertLegacySnapshotAsync(paths, factory, version: 1, isActive: true);
        await repository.SetSettingAsync("app.lastVersion", "1.0.6");

        await ConfigurationBootstrapper.PrepareAsync(repository, LegacyFactoryDefault(), "1.0.7");

        var current = await repository.GetCurrentAsync();
        Assert.NotNull(current);
        Assert.Equal(configured.Id, current.Id);
        Assert.Null(await repository.GetLegacyConfigurationForMigrationAsync());
        Assert.True(Directory.GetFiles(paths.BackupDirectory, "config-*.db").Length > 0);
    }

    [Fact]
    public async Task QueryEditor_OffersEveryElementAndPersistsCompletedLayoutOrder()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var active = TestConfig.CreateElements("visible_input", "readonly_result");
        active.Elements[0].Role = ElementRole.VisibleField;
        active.Elements[1].Role = ElementRole.Hidden;
        active.Elements[1].CanInput = false;
        await repository.SaveCurrentAsync(active);
        var viewModel = new QueryViewModel(CreateServices(paths, repository));

        await viewModel.LoadAsync();

        Assert.Contains(viewModel.AvailableConditions, x => x.Key == "readonly_result");
        viewModel.ToggleConditionsEditCommand.Execute(null);
        Assert.True(viewModel.IsEditingConditions);
        viewModel.AddConditionCommand.Execute(viewModel.AvailableConditions.Single(x => x.Key == "readonly_result"));
        var added = viewModel.Fields.Single(x => x.Definition.Key == "readonly_result");
        Assert.False(added.IsEditorVisible);

        viewModel.MoveCondition(added, viewModel.Fields.First());
        Assert.Equal("readonly_result", viewModel.Fields.First().Definition.Key);
        await viewModel.ToggleConditionsEditAsync();
        viewModel.MoveCondition(added, viewModel.Fields.Last());
        Assert.Equal("readonly_result", viewModel.Fields.First().Definition.Key);

        var reopened = new QueryViewModel(CreateServices(paths, new SqliteConfigurationRepository(paths)));
        await reopened.LoadAsync();
        Assert.Equal(["readonly_result", "visible_input"], reopened.Fields.Select(x => x.Definition.Key));
    }

    [Fact]
    public async Task ElementSaveMessage_IsPageScopedAndOmitsCycleWarnings()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var current = TestConfig.CreateElements("a", "b");
        current.Rules.Add(TestConfig.Rule("a 到 b", "a", "b"));
        current.Rules.Add(TestConfig.Rule("b 到 a", "b", "a"));
        await repository.SaveCurrentAsync(current);
        var viewModel = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await viewModel.LoadCurrentAsync();

        await viewModel.SaveAsync("elements");

        Assert.Equal("配置已保存。", viewModel.ElementStatusMessage);
        Assert.DoesNotContain("受控循环", viewModel.ElementStatusMessage);
        Assert.DoesNotContain("配置已保存", viewModel.StatusMessage);
    }

    [Fact]
    public async Task QueryReload_WithUnchangedConfiguration_PreservesResultsAndShowsObjectAsSource()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var current = TestConfig.CreateElements("patient_id", "patient_name");
        current.Elements.Single(x => x.Key == "patient_name").CanInput = false;
        var entry = new QueryEntryDefinition
        {
            Name = "按患者编号查询",
            FilterTemplate = "PATIENT_ID={{patient_id}}"
        };
        current.QueryObjects =
        [
            new QueryObjectDefinition
            {
                Name = "患者基本信息",
                BaseSqlTemplate = "SELECT PATIENT_NAME FROM Patient",
                OutputMappings = [new OutputMapping { ColumnName = "PATIENT_NAME", ElementKey = "patient_name" }],
                Entries = [entry]
            }
        ];
        current.Rules = QueryObjectCompiler.Compile(current.QueryObjects).ToList();
        await repository.SaveCurrentAsync(current);

        var fake = new FakeExecutor
        {
            [entry.RuntimeRuleId] = _ =>
            [
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PATIENT_NAME"] = "张三"
                }
            ]
        };
        var profiles = new ConnectionProfileRepository(repository);
        var credentials = new DpapiCredentialStore(repository);
        var queryExecutor = new OdbcQueryExecutor(profiles, credentials);
        var logger = new ExecutionMetadataLogger(paths);
        var services = new ApplicationServices(paths, repository, profiles, credentials, queryExecutor,
            new RuleScheduler(fake, logger), logger, new ConfigurationPackageService());
        var viewModel = new QueryViewModel(services);
        await viewModel.LoadAsync();
        viewModel.Fields.Single(x => x.Definition.Key == "patient_id").InputText = "P001";

        await viewModel.StartNewRunAsync();

        var resultField = viewModel.Fields.Single(x => x.Definition.Key == "patient_name");
        Assert.Equal("张三", resultField.InputText);
        Assert.Equal("患者基本信息", resultField.SourceLabel);
        Assert.NotEmpty(viewModel.TraceItems);
        var resultFieldBeforeReload = resultField;
        var traceCount = viewModel.TraceItems.Count;
        var statusBeforeReload = viewModel.StatusSummary;

        await viewModel.LoadAsync();

        Assert.Same(resultFieldBeforeReload, viewModel.Fields.Single(x => x.Definition.Key == "patient_name"));
        Assert.Equal("张三", resultFieldBeforeReload.InputText);
        Assert.Equal("患者基本信息", resultFieldBeforeReload.SourceLabel);
        Assert.Equal(traceCount, viewModel.TraceItems.Count);
        Assert.Equal(statusBeforeReload, viewModel.StatusSummary);
    }

    [Fact]
    public async Task ConnectionSettings_ValidateAndSaveWithoutOpeningDatabaseConnection()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var services = CreateServices(paths, repository);
        var viewModel = new ConnectionViewModel(services)
        {
            DriverName = null!, Dsn = null!, Host = null!, Namespace = null!, Username = null!
        };

        var invalidException = await Record.ExceptionAsync(viewModel.SaveAsync);
        Assert.Null(invalidException);
        Assert.Contains("请选择", viewModel.NotificationMessage);

        viewModel.DriverName = "InterSystems IRIS ODBC35";
        viewModel.Host = "iris.local";
        viewModel.Port = 1972;
        viewModel.Namespace = "HOSPITAL";
        viewModel.Username = "readonly";
        viewModel.Password = "saved-password";
        viewModel.SavePassword = true;
        await viewModel.SaveAsync();

        var stored = await services.Profiles.GetAsync();
        Assert.Equal("iris.local", stored.Host);
        Assert.Equal("readonly", stored.Username);
        Assert.Equal("saved-password", await services.Credentials.GetAsync());
        Assert.Contains("连接配置已保存", viewModel.NotificationMessage);
    }

    [Fact]
    public async Task RapidSaveRequests_DoNotWriteCurrentConfigurationConcurrently()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var active = TestConfig.CreateElements("patient_id");
        await repository.SaveCurrentAsync(active);
        var viewModel = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await viewModel.LoadCurrentAsync();
        viewModel.Elements.Single().Label = "患者编号";

        var firstSave = viewModel.SaveAsync();
        var secondSave = viewModel.SaveAsync();
        var exception = await Record.ExceptionAsync(() => Task.WhenAll(firstSave, secondSave));

        Assert.Null(exception);
        Assert.Equal("患者编号", (await repository.GetCurrentAsync())!.Elements.Single().Label);
    }

    [Fact]
    public async Task OutputMappingOptions_UseSelectColumnsAndExcludeElementsMappedByOtherRows()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var active = TestConfig.CreateElements("registration_no", "patient_name", "visit_number");
        active.Rules.Add(new QueryRuleDefinition
        {
            Name = "patient lookup",
            SqlTemplate = "SELECT REG_NO AS registration_no, NAME AS patient_name, VISIT_NO AS visit_number FROM Patient",
            OutputMappings = [new OutputMapping { ColumnName = "registration_no", ElementKey = "registration_no" }]
        });
        await repository.SaveCurrentAsync(active);
        var viewModel = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await viewModel.LoadCurrentAsync();

        viewModel.AddMappingCommand.Execute(null);

        var mappings = viewModel.SelectedQueryObject!.OutputMappings;
        var added = mappings.Last();
        Assert.Equal("patient_name", added.ColumnName);
        Assert.Equal("patient_name", added.ElementKey);
        Assert.DoesNotContain(viewModel.GetAvailableOutputElements(added), x => x.Key == "registration_no");
        Assert.Contains(viewModel.GetAvailableOutputElements(added), x => x.Key == "patient_name");
        Assert.Equal(["registration_no", "patient_name", "visit_number"], viewModel.SelectedRuleResultColumns);

        viewModel.SelectedQueryObject.BaseSqlTemplate = "SELECT * FROM Patient";
        viewModel.RefreshSelectedRuleResultColumns();
        Assert.Contains("registration_no", viewModel.SelectedRuleResultColumns);
    }

    [Fact]
    public async Task RuleTestValues_AutoSaveAndRestoreByRule()
    {
        var paths = new AppDataPaths(_temp);
        var repository = new SqliteConfigurationRepository(paths);
        await repository.InitializeAsync();
        var active = TestConfig.CreateElements("id_card_no", "registration_no");
        active.Rules.Add(TestConfig.Rule("lookup", "id_card_no", "registration_no"));
        await repository.SaveCurrentAsync(active);
        var first = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await first.LoadCurrentAsync();

        first.TestValues = "id_card_no=110101199001011234";
        await Task.Delay(800);

        var reopened = new DraftConfigurationViewModel(CreateServices(paths, repository));
        await reopened.LoadCurrentAsync();
        for (var attempt = 0; attempt < 20 && !reopened.TestValues.Contains("110101199001011234"); attempt++)
            await Task.Delay(50);
        Assert.Equal("id_card_no=110101199001011234", reopened.TestValues);
    }

    private static ApplicationServices CreateServices(AppDataPaths paths, SqliteConfigurationRepository repository)
    {
        var profiles = new ConnectionProfileRepository(repository);
        var credentials = new DpapiCredentialStore(repository);
        var executor = new OdbcQueryExecutor(profiles, credentials);
        var logger = new ExecutionMetadataLogger(paths);
        return new ApplicationServices(paths, repository, profiles, credentials, executor,
            new RuleScheduler(executor, logger), logger, new ConfigurationPackageService());
    }

    private static ConfigurationSnapshot LegacyFactoryDefault() => new()
    {
        Name = "初始业务元素",
        Elements = [new ElementDefinition { Key = "id_card_no", Label = "身份证号" }]
    };

    private static async Task InsertLegacySnapshotAsync(AppDataPaths paths, ConfigurationSnapshot snapshot, int version, bool isActive)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshots(id,version,name,is_active,created_at,json)
            VALUES($id,$version,$name,$active,$created,$json)
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$name", snapshot.Name);
        command.Parameters.AddWithValue("$active", isActive ? 1 : 0);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
