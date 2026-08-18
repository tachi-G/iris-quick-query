using System.Collections.ObjectModel;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;
using IrisQuickQuery.Infrastructure.Security;
using Microsoft.Win32;

namespace IrisQuickQuery.App.ViewModels;

public sealed class DraftConfigurationViewModel : ObservableObject
{
    private readonly ApplicationServices _services;
    private readonly Dictionary<Guid, string> _liveValidatedRules = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly RuleTestValueStore _ruleTestValueStore;
    private ConfigurationSnapshot? _source;
    private ElementDefinition? _selectedElement;
    private QueryRuleDefinition? _selectedRule;
    private OutputMapping? _selectedMapping;
    private string _name = string.Empty;
    private string _statusMessage = string.Empty;
    private string _testValues = string.Empty;
    private string _selectedRuleTestStatus = "请完整配置并测试当前规则。";
    private DataView? _testPreview;
    private CancellationTokenSource? _testValueSaveDebounce;
    private int _testValueLoadVersion;
    private bool _suppressTestValuePersistence;

    public ObservableCollection<ElementDefinition> Elements { get; } = [];
    public ObservableCollection<QueryRuleDefinition> Rules { get; } = [];
    public ObservableCollection<string> SelectedRuleResultColumns { get; } = [];
    public Array ElementTypes => Enum.GetValues(typeof(ElementDataType));
    public Array ElementRoles => Enum.GetValues(typeof(ElementRole));
    public Array ResultModes => Enum.GetValues(typeof(RuleResultMode));
    public string Name { get => _name; set => Set(ref _name, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string TestValues
    {
        get => _testValues;
        set
        {
            if (!Set(ref _testValues, value) || _suppressTestValuePersistence) return;
            _testValueLoadVersion++;
            ScheduleTestValueSave();
        }
    }
    public string SelectedRuleTestStatus { get => _selectedRuleTestStatus; set => Set(ref _selectedRuleTestStatus, value); }
    public DataView? TestPreview { get => _testPreview; set => Set(ref _testPreview, value); }
    public ElementDefinition? SelectedElement
    {
        get => _selectedElement;
        set
        {
            if (!Set(ref _selectedElement, value)) return;
            RemoveElementCommand?.RaiseCanExecuteChanged();
        }
    }
    public QueryRuleDefinition? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (_selectedRule is not null && !_suppressTestValuePersistence)
                _ = SaveRuleTestValuesSafelyAsync(_selectedRule.Id, TestValues);
            if (!Set(ref _selectedRule, value)) return;
            SelectedMapping = null;
            TestPreview = null;
            Raise(nameof(SelectedRuleMappings));
            RefreshSelectedRuleResultColumns();
            BeginLoadSelectedRuleTestValues(value);
            RefreshSelectedRuleTestStatus();
            RemoveRuleCommand?.RaiseCanExecuteChanged();
            AddMappingCommand?.RaiseCanExecuteChanged();
            RemoveMappingCommand?.RaiseCanExecuteChanged();
            TestRuleCommand?.RaiseCanExecuteChanged();
            RefreshTestValuesCommand?.RaiseCanExecuteChanged();
        }
    }
    public OutputMapping? SelectedMapping
    {
        get => _selectedMapping;
        set
        {
            if (!Set(ref _selectedMapping, value)) return;
            RemoveMappingCommand?.RaiseCanExecuteChanged();
        }
    }
    public ListCollectionView? SelectedRuleMappings => SelectedRule is null ? null : (ListCollectionView)CollectionViewSource.GetDefaultView(SelectedRule.OutputMappings);

    public RelayCommand AddElementCommand { get; }
    public RelayCommand RemoveElementCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public RelayCommand AddMappingCommand { get; }
    public RelayCommand RemoveMappingCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand TestRuleCommand { get; }
    public RelayCommand RefreshTestValuesCommand { get; }
    public AsyncRelayCommand ImportRulesCommand { get; }
    public AsyncRelayCommand ExportRulesCommand { get; }

    public DraftConfigurationViewModel(ApplicationServices services)
    {
        _services = services;
        _ruleTestValueStore = new RuleTestValueStore(services.Configurations);
        AddElementCommand = new RelayCommand(AddElement);
        RemoveElementCommand = new RelayCommand(RemoveElement, () => SelectedElement is not null);
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => SelectedRule is not null);
        AddMappingCommand = new RelayCommand(AddMapping, () => SelectedRule is not null);
        RemoveMappingCommand = new RelayCommand(RemoveMapping, () => SelectedRule is not null && SelectedMapping is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        TestRuleCommand = new AsyncRelayCommand(TestSelectedRuleAsync, () => SelectedRule is not null);
        RefreshTestValuesCommand = new RelayCommand(RefreshTestTemplate, () => SelectedRule is not null);
        ImportRulesCommand = new AsyncRelayCommand(ImportRulesAsync);
        ExportRulesCommand = new AsyncRelayCommand(ExportRulesAsync);
    }

    public async Task LoadCurrentAsync()
    {
        var current = await _services.Configurations.GetCurrentAsync() ?? new ConfigurationSnapshot();
        _source = Clone(current);
        Name = current.Name;
        Elements.Clear(); foreach (var item in current.Elements) Elements.Add(Clone(item));
        Rules.Clear(); foreach (var item in current.Rules) Rules.Add(Clone(item));
        _liveValidatedRules.Clear();
        SelectedElement = Elements.FirstOrDefault(); SelectedRule = Rules.FirstOrDefault();
        RefreshSelectedRuleTestStatus();
        StatusMessage = "已加载当前配置。";
    }

    public ConfigurationSnapshot BuildSnapshot()
    {
        var snapshot = new ConfigurationSnapshot
        {
            Id = _source?.Id ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(Name) ? "未命名配置" : Name.Trim(),
            Elements = Elements.Select(Clone).ToList(),
            Rules = Rules.Select(Clone).ToList()
        };
        return snapshot;
    }

    private void AddElement()
    {
        var index = 1;
        while (Elements.Any(x => string.Equals(x.Key, $"new_element_{index}", StringComparison.OrdinalIgnoreCase))) index++;
        var item = new ElementDefinition { Key = $"new_element_{index}", Label = "新元素", DisplayOrder = Elements.Count * 10 + 10 };
        Elements.Add(item); SelectedElement = item;
    }

    private void RemoveElement()
    {
        if (SelectedElement is null) return;
        var key = SelectedElement.Key;
        if (Rules.Any(x => (x.SqlTemplate ?? string.Empty).Contains("{{" + key + "}}", StringComparison.OrdinalIgnoreCase)
                           || x.OutputMappings.Any(m => string.Equals(m.ElementKey, key, StringComparison.OrdinalIgnoreCase))))
        {
            StatusMessage = "该元素仍被 SQL 规则引用，不能删除。"; return;
        }
        Elements.Remove(SelectedElement); SelectedElement = Elements.FirstOrDefault();
    }

    private void AddRule()
    {
        var item = new QueryRuleDefinition { Name = "新查询规则", SqlTemplate = "SELECT column_name\nFROM schema.table_name\nWHERE input_column = {{element_key}}", DisplayOrder = Rules.Count * 10 + 10 };
        Rules.Add(item); SelectedRule = item;
    }

    private void RemoveRule()
    {
        if (SelectedRule is null) return;
        var removed = SelectedRule;
        Rules.Remove(removed);
        _suppressTestValuePersistence = true;
        try { SelectedRule = Rules.FirstOrDefault(); }
        finally { _suppressTestValuePersistence = false; }
        _ = SaveRuleTestValuesSafelyAsync(removed.Id, string.Empty);
    }
    private void AddMapping()
    {
        if (SelectedRule is null) return;
        RefreshSelectedRuleResultColumns();
        var usedColumns = SelectedRule.OutputMappings.Select(x => x.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedElements = SelectedRule.OutputMappings.Select(x => x.ElementKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnName = SelectedRuleResultColumns.FirstOrDefault(x => !usedColumns.Contains(x))
                         ?? SelectedRuleResultColumns.FirstOrDefault()
                         ?? string.Empty;
        var elementKey = Elements.FirstOrDefault(x => !usedElements.Contains(x.Key))?.Key ?? string.Empty;
        SelectedRule.OutputMappings.Add(new OutputMapping { ColumnName = columnName, ElementKey = elementKey });
        Raise(nameof(SelectedRuleMappings)); SelectedRuleMappings?.Refresh();
    }
    private void RemoveMapping()
    {
        if (SelectedRule is null || SelectedMapping is null) return;
        SelectedRule.OutputMappings.Remove(SelectedMapping); SelectedMapping = null; Raise(nameof(SelectedRuleMappings)); SelectedRuleMappings?.Refresh();
    }

    public void RefreshSelectedRuleResultColumns()
    {
        var parsed = SqlSelectListParser.GetResultColumnNames(SelectedRule?.SqlTemplate ?? string.Empty);
        SelectedRuleResultColumns.Clear();
        foreach (var column in parsed) SelectedRuleResultColumns.Add(column);
        if (SelectedRule is null) return;
        foreach (var column in SelectedRule.OutputMappings.Select(x => x.ColumnName).Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!SelectedRuleResultColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) SelectedRuleResultColumns.Add(column);
    }

    public IReadOnlyList<string> GetResultColumnOptions(OutputMapping mapping)
    {
        RefreshSelectedRuleResultColumns();
        var result = SelectedRuleResultColumns.ToList();
        if (!string.IsNullOrWhiteSpace(mapping.ColumnName)
            && !result.Contains(mapping.ColumnName, StringComparer.OrdinalIgnoreCase)) result.Add(mapping.ColumnName);
        return result;
    }

    public IReadOnlyList<ElementDefinition> GetAvailableOutputElements(OutputMapping mapping)
    {
        if (SelectedRule is null) return [];
        var usedByOtherRows = SelectedRule.OutputMappings
            .Where(x => !ReferenceEquals(x, mapping) && !string.IsNullOrWhiteSpace(x.ElementKey))
            .Select(x => x.ElementKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Elements.Where(x => !usedByOtherRows.Contains(x.Key)
                                   || string.Equals(x.Key, mapping.ElementKey, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
        var synchronizedRuleCount = SynchronizeRenamedElementKeys();
        var snapshot = BuildSnapshot();
        var issues = ConfigurationValidator.Validate(snapshot);
        var errors = issues.Where(x => !x.IsWarning).ToArray();
        if (errors.Length > 0) { StatusMessage = string.Join(Environment.NewLine, errors.Take(8).Select(x => "• " + x.Message)); return; }
        await _services.Configurations.SaveCurrentAsync(snapshot);
        StatusMessage = "配置已保存，并立即用于后续查询。";
        _source = Clone(snapshot);
        if (synchronizedRuleCount > 0)
            StatusMessage += $" 已同步 {synchronizedRuleCount} 条 SQL 规则中的全局元素引用。";
        if (issues.Any(x => x.IsWarning)) StatusMessage += Environment.NewLine + string.Join(Environment.NewLine, issues.Where(x => x.IsWarning).Select(x => "提示：" + x.Message));
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败：" + ex.Message;
        }
        finally { _saveGate.Release(); }
    }

    private async Task TestSelectedRuleAsync()
    {
        var rule = SelectedRule;
        if (rule is null) return;
        try
        {
            var safety = SqlSafetyValidator.Validate(rule.SqlTemplate).Where(x => !x.IsWarning).ToArray();
            if (safety.Length > 0) { StatusMessage = string.Join(Environment.NewLine, safety.Select(x => x.Message)); return; }
            var compiled = SqlTemplateCompiler.Compile(rule.SqlTemplate);
            var rawValues = ParseTestValues(TestValues);
            var parameters = new List<QueryParameter>();
            foreach (var key in compiled.ParameterKeys)
            {
                var element = Elements.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"找不到输入元素 {key}。");
                if (!rawValues.TryGetValue(key, out var raw)) throw new InvalidOperationException($"测试参数缺少 {key}=值。");
                if (!ElementValueConverter.TryConvert(raw, element, out var converted, out var error) || converted is null)
                    throw new InvalidOperationException(error ?? $"测试参数 {key} 无效。");
                parameters.Add(new QueryParameter(key, element.EffectiveDataType, converted));
            }
            var result = await _services.QueryExecutor.ExecuteAsync(new QueryExecutionRequest(rule, compiled.CommandText, parameters,
                Math.Clamp(rule.TimeoutSeconds, 1, 15), Math.Min(rule.MaxRows, 50)), CancellationToken.None);
            var missingColumns = rule.OutputMappings.Select(x => x.ColumnName)
                .Where(name => !result.Columns.Contains(name, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (missingColumns.Length > 0)
                throw new InvalidOperationException("结果中缺少已映射列：" + string.Join("、", missingColumns));
            TestPreview = ToDataView(result.Rows);
            _liveValidatedRules[rule.Id] = HashRule(rule);
            SelectedRuleTestStatus = "已通过当前连接测试。若再次修改 SQL、映射或规则设置，需要重新测试。";
            StatusMessage = result.WasTruncated ? "测试成功，预览已截断。" : $"测试成功：返回 {result.Rows.Count} 行。输出映射仍需与预览列名一致。";
        }
        catch (Exception ex)
        {
            SelectedRuleTestStatus = "尚未通过当前连接测试。";
            StatusMessage = "测试失败：" + ex.Message;
        }
    }

    private void RefreshTestTemplate()
    {
        if (SelectedRule is null) { SetTestValuesWithoutSaving(string.Empty); return; }
        try
        {
            var keys = SqlTemplateCompiler.Compile(SelectedRule.SqlTemplate ?? string.Empty).ParameterKeys.Distinct(StringComparer.OrdinalIgnoreCase);
            var existing = ParseTestValues(TestValues);
            TestValues = string.Join(Environment.NewLine, keys.Select(x => x + "=" + (existing.TryGetValue(x, out var value) ? value : string.Empty)));
        }
        catch { SetTestValuesWithoutSaving(string.Empty); }
    }

    private void BeginLoadSelectedRuleTestValues(QueryRuleDefinition? rule)
    {
        var version = ++_testValueLoadVersion;
        _testValueSaveDebounce?.Cancel();
        SetTestValuesWithoutSaving(rule is null ? string.Empty : BuildTestTemplate(rule, null));
        if (rule is not null) _ = LoadSelectedRuleTestValuesAsync(rule, version);
    }

    private async Task LoadSelectedRuleTestValuesAsync(QueryRuleDefinition rule, int version)
    {
        try
        {
            var saved = await _ruleTestValueStore.GetAsync(rule.Id);
            if (version != _testValueLoadVersion || SelectedRule?.Id != rule.Id) return;
            SetTestValuesWithoutSaving(BuildTestTemplate(rule, saved));
        }
        catch (Exception ex)
        {
            if (version == _testValueLoadVersion) StatusMessage = "无法读取已保存的测试参数：" + ex.Message;
        }
    }

    private void ScheduleTestValueSave()
    {
        if (SelectedRule is null) return;
        _testValueSaveDebounce?.Cancel();
        _testValueSaveDebounce?.Dispose();
        _testValueSaveDebounce = new CancellationTokenSource();
        _ = SaveRuleTestValuesAfterDelayAsync(SelectedRule.Id, TestValues, _testValueSaveDebounce.Token);
    }

    private async Task SaveRuleTestValuesAfterDelayAsync(Guid ruleId, string values, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            await _ruleTestValueStore.SaveAsync(ruleId, values, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = "自动保存测试参数失败：" + ex.Message; }
    }

    private async Task SaveRuleTestValuesSafelyAsync(Guid ruleId, string values)
    {
        try { await _ruleTestValueStore.SaveAsync(ruleId, values); }
        catch (Exception ex) { StatusMessage = "自动保存测试参数失败：" + ex.Message; }
    }

    private void SetTestValuesWithoutSaving(string value)
    {
        _suppressTestValuePersistence = true;
        try { TestValues = value; }
        finally { _suppressTestValuePersistence = false; }
    }

    private static string BuildTestTemplate(QueryRuleDefinition rule, string? saved)
    {
        var existing = string.IsNullOrWhiteSpace(saved) ? [] : ParseTestValues(saved);
        var keys = SqlTemplateCompiler.Compile(rule.SqlTemplate ?? string.Empty).ParameterKeys.Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(Environment.NewLine,
            keys.Select(x => x + "=" + (existing.TryGetValue(x, out var value) ? value : string.Empty)));
    }

    private void RefreshSelectedRuleTestStatus()
    {
        if (SelectedRule is null)
        {
            SelectedRuleTestStatus = "请先从左侧选择一条规则。";
            return;
        }
        SelectedRuleTestStatus = _liveValidatedRules.TryGetValue(SelectedRule.Id, out var hash) && hash == HashRule(SelectedRule)
            ? "已通过当前连接测试。若再次修改 SQL、映射或规则设置，需要重新测试。"
            : "尚未测试。请完整配置 SQL、输出映射和测试参数后测试当前规则。";
    }

    private int SynchronizeRenamedElementKeys()
    {
        if (_source is null) return 0;
        var changes = ElementKeySynchronizer.Detect(_source.Elements, Elements);
        if (changes.Count == 0) return 0;

        var expectedElements = _source.Elements.Select(Clone).ToList();
        foreach (var change in changes)
        {
            var element = expectedElements.FirstOrDefault(x => x.Id == change.ElementId);
            if (element is not null) element.Key = change.NewKey;
        }
        var expectedRules = _source.Rules.Select(Clone).ToList();
        ElementKeySynchronizer.Apply(expectedRules, changes);
        var expectedRulesById = expectedRules.ToDictionary(x => x.Id);
        var sourceRulesById = _source.Rules.ToDictionary(x => x.Id);

        var updatedCount = ElementKeySynchronizer.Apply(Rules, changes);
        foreach (var currentRule in Rules)
        {
            if (!_liveValidatedRules.TryGetValue(currentRule.Id, out var validatedHash)
                || !sourceRulesById.TryGetValue(currentRule.Id, out var sourceRule)
                || !expectedRulesById.TryGetValue(currentRule.Id, out var expectedRule)
                || validatedHash != HashRule(sourceRule, _source.Elements)
                || JsonSerializer.Serialize(currentRule) != JsonSerializer.Serialize(expectedRule)
                || HashRule(currentRule, Elements) != HashRule(expectedRule, expectedElements)) continue;

            // Renaming a stable element key does not alter the compiled SQL, parameter type, or output target.
            _liveValidatedRules[currentRule.Id] = HashRule(currentRule, Elements);
        }
        RefreshTestTemplate();
        RefreshSelectedRuleTestStatus();
        return updatedCount;
    }

    private async Task ExportRulesAsync()
    {
        if (Rules.Count == 0)
        {
            StatusMessage = "当前没有可导出的 SQL 规则。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "IRIS SQL 规则包 (*.irisqconfig)|*.irisqconfig",
            FileName = $"IRIS-SQL规则-{DateTime.Now:yyyyMMdd-HHmm}.irisqconfig"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var elementCount = await _services.Packages.ExportRulesAsync(Rules.ToArray(), Elements.ToArray(), dialog.FileName);
            StatusMessage = $"已导出 {Rules.Count} 条 SQL 规则及其使用的 {elementCount} 个全局元素。规则包不包含服务器连接、密码、测试参数或患者数据。";
        }
        catch (Exception ex) { StatusMessage = "导出规则失败：" + ex.Message; }
    }

    private async Task ImportRulesAsync()
    {
        var dialog = new OpenFileDialog { Filter = "IRIS SQL 规则包 (*.irisqconfig)|*.irisqconfig" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = await _services.Packages.ImportRulesAsync(dialog.FileName);
            ElementImportPlan? mergePlan = null;
            string confirmation;
            if (imported.IncludesElements)
            {
                mergePlan = ElementImportPlanner.Create(Elements.ToArray(), imported.Elements.ToArray());
                if (!mergePlan.CanApply)
                {
                    var details = string.Join(Environment.NewLine, mergePlan.Conflicts.Take(8).Select(x => "• " + x));
                    StatusMessage = "导入已停止：规则包中的全局元素与本机配置存在身份冲突。" + Environment.NewLine + details;
                    MessageBox.Show(StatusMessage, "无法导入规则包", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                confirmation = $"规则 {imported.Rules.Count} 条：将替换当前 {Rules.Count} 条规则。{Environment.NewLine}"
                               + $"依赖全局元素 {imported.Elements.Count} 个：新增 {mergePlan.AddedCount} 个、更新 {mergePlan.UpdatedCount} 个、复用 {mergePlan.ReusedCount} 个。{Environment.NewLine}"
                               + $"本机其他 {mergePlan.UntouchedLocalCount} 个全局元素保持不变。{Environment.NewLine}{Environment.NewLine}"
                               + "导入内容将先载入编辑区，点击“保存”后才会正式生效。是否继续？";
            }
            else
            {
                var legacyMissing = GetReferencedElementKeys(imported.Rules)
                    .Where(x => !Elements.Any(element => string.Equals(element.Key, x, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(x => x).ToArray();
                confirmation = $"这是旧版规则包，将用 {imported.Rules.Count} 条规则替换当前 {Rules.Count} 条规则。{Environment.NewLine}"
                               + "旧版规则包不包含全局元素，本机现有元素不会改变。"
                               + (legacyMissing.Length == 0 ? string.Empty : Environment.NewLine + "本机仍缺少：" + string.Join("、", legacyMissing))
                               + $"{Environment.NewLine}{Environment.NewLine}导入内容将先载入编辑区，点击“保存”后才会正式生效。是否继续？";
            }

            if (MessageBox.Show(confirmation, "确认导入规则包", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                StatusMessage = "已取消导入，当前编辑区没有变化。";
                return;
            }

            if (mergePlan is not null)
            {
                Elements.Clear();
                foreach (var element in mergePlan.MergedElements) Elements.Add(Clone(element));
                SelectedElement = Elements.FirstOrDefault();
            }
            Rules.Clear();
            foreach (var rule in imported.Rules) Rules.Add(Clone(rule));
            _liveValidatedRules.Clear();
            SelectedRule = Rules.FirstOrDefault();

            var localKeys = Elements.Select(x => x.Key).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var referencedKeys = GetReferencedElementKeys(Rules);
            var missing = referencedKeys.Where(x => !localKeys.Contains(x)).OrderBy(x => x).ToArray();
            StatusMessage = $"已把 {Rules.Count} 条规则载入当前编辑区，并替换编辑区原有规则；"
                + (mergePlan is null
                    ? "旧版规则包未包含全局元素，本机元素保持不变；"
                    : $"全局元素已在编辑区合并：新增 {mergePlan.AddedCount} 个、更新 {mergePlan.UpdatedCount} 个、复用 {mergePlan.ReusedCount} 个；")
                + "尚未保存，也不会改变本机服务器连接。"
                + (missing.Length == 0
                    ? " 请检查后点击保存。"
                    : " 本机缺少这些业务元素，请先补充：" + string.Join("、", missing));
        }
        catch (Exception ex) { StatusMessage = "导入规则失败：" + ex.Message; }
    }

    private static HashSet<string> GetReferencedElementKeys(IEnumerable<QueryRuleDefinition> rules)
    {
        var referencedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            try
            {
                foreach (var key in SqlTemplateCompiler.Compile(rule.SqlTemplate ?? string.Empty).ParameterKeys) referencedKeys.Add(key);
            }
            catch { /* Static validation will show malformed SQL after an old package is imported. */ }
            foreach (var mapping in rule.OutputMappings)
                if (!string.IsNullOrWhiteSpace(mapping.ElementKey)) referencedKeys.Add(mapping.ElementKey);
        }
        return referencedKeys;
    }

    private static Dictionary<string, string> ParseTestValues(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim(), x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static DataView? ToDataView(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return null;
        var table = new DataTable();
        foreach (var key in rows.SelectMany(x => x.Keys).Distinct(StringComparer.OrdinalIgnoreCase)) table.Columns.Add(key);
        foreach (var source in rows) { var row = table.NewRow(); foreach (var pair in source) row[pair.Key] = pair.Value ?? DBNull.Value; table.Rows.Add(row); }
        return table.DefaultView;
    }

    private string HashRule(QueryRuleDefinition rule) => HashRule(rule, Elements);

    private static string HashRule(QueryRuleDefinition rule, IEnumerable<ElementDefinition> elements)
    {
        var referencedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var key in SqlTemplateCompiler.Compile(rule.SqlTemplate ?? string.Empty).ParameterKeys) referencedKeys.Add(key);
        }
        catch { /* Invalid SQL will fail static validation before activation. */ }
        foreach (var mapping in rule.OutputMappings)
            if (!string.IsNullOrWhiteSpace(mapping.ElementKey)) referencedKeys.Add(mapping.ElementKey);
        var elementTypes = elements
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && referencedKeys.Contains(x.Key))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { x.Key, DataType = x.EffectiveDataType });
        var payload = JsonSerializer.Serialize(new { Rule = rule, ElementTypes = elementTypes });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
    private static ConfigurationSnapshot Clone(ConfigurationSnapshot value) => JsonSerializer.Deserialize<ConfigurationSnapshot>(JsonSerializer.Serialize(value))!;
    private static ElementDefinition Clone(ElementDefinition value) => JsonSerializer.Deserialize<ElementDefinition>(JsonSerializer.Serialize(value))!;
    private static QueryRuleDefinition Clone(QueryRuleDefinition value) => JsonSerializer.Deserialize<QueryRuleDefinition>(JsonSerializer.Serialize(value))!;
}
