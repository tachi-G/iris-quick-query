using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using IrisQuickQuery.App.Services;
using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;

namespace IrisQuickQuery.App.ViewModels;

public sealed class QueryViewModel : ObservableObject
{
    private const string ConditionLayoutSettingKey = "query.conditionElementIds.v1";
    private readonly ApplicationServices _services;
    private ConfigurationSnapshot _snapshot = new();
    private RunContext? _context;
    private CancellationTokenSource? _runCancellation;
    private string _statusSummary = "输入任意人工字段后按 Enter 开始查询。";
    private bool _isRunning;
    private bool _hasConditionSelection;
    private bool _isEditingConditions;
    private bool _isLoaded;
    private readonly Dictionary<string, ElementFieldViewModel> _fieldCache = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ElementFieldViewModel> Fields { get; } = [];
    public ObservableCollection<QueryConditionOptionViewModel> AvailableConditions { get; } = [];
    public ObservableCollection<ListResultViewModel> ListResults { get; } = [];
    public ObservableCollection<TraceItemViewModel> TraceItems { get; } = [];
    public string StatusSummary { get => _statusSummary; set => Set(ref _statusSummary, value); }
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!Set(ref _isRunning, value)) return;
            Raise(nameof(IsNotRunning));
            ToggleConditionsEditCommand.RaiseCanExecuteChanged();
            AddConditionCommand.RaiseCanExecuteChanged();
            RemoveConditionCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsNotRunning => !IsRunning;
    public bool IsEditingConditions
    {
        get => _isEditingConditions;
        private set
        {
            if (!Set(ref _isEditingConditions, value)) return;
            Raise(nameof(ConditionsEditButtonText));
        }
    }
    public string ConditionsEditButtonText => IsEditingConditions ? "完成" : "编辑";
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncRelayCommand ToggleConditionsEditCommand { get; }
    public RelayCommand AddConditionCommand { get; }
    public RelayCommand RemoveConditionCommand { get; }

    public QueryViewModel(ApplicationServices services)
    {
        _services = services;
        RunCommand = new AsyncRelayCommand(StartNewRunAsync);
        CancelCommand = new RelayCommand(() => _runCancellation?.Cancel());
        ClearCommand = new RelayCommand(Clear);
        ToggleConditionsEditCommand = new AsyncRelayCommand(ToggleConditionsEditAsync, () => !IsRunning);
        AddConditionCommand = new RelayCommand(AddCondition, parameter => !IsRunning && parameter is QueryConditionOptionViewModel);
        RemoveConditionCommand = new RelayCommand(RemoveCondition, parameter => !IsRunning && parameter is ElementFieldViewModel);
    }

    public async Task LoadAsync()
    {
        var current = await _services.Configurations.GetCurrentAsync() ?? new ConfigurationSnapshot();
        if (_isLoaded && current.Id == _snapshot.Id && current.CreatedAt == _snapshot.CreatedAt) return;

        _runCancellation?.Cancel();
        if (_context is not null) _context.Changed -= ContextOnChanged;
        _context = null;
        IsRunning = false;
        ListResults.Clear();
        TraceItems.Clear();
        _snapshot = current;
        var previousManual = _fieldCache.Values.Where(x => x.IsManual)
            .ToDictionary(x => x.Definition.Key, x => (object?)x.InputText, StringComparer.OrdinalIgnoreCase);
        var selectedIds = _hasConditionSelection
            ? Fields.Select(x => x.Definition.Id).ToList()
            : await LoadConditionLayoutAsync();
        Fields.Clear();
        _fieldCache.Clear();
        foreach (var definition in _snapshot.Elements.OrderBy(x => x.Group).ThenBy(x => x.DisplayOrder))
        {
            var field = new ElementFieldViewModel(definition);
            if (previousManual.TryGetValue(definition.Key, out var value)) field.SetInitialManual(value?.ToString() ?? string.Empty);
            _fieldCache[definition.Key] = field;
        }
        if (selectedIds is null)
        {
            foreach (var definition in _snapshot.Elements.OrderBy(x => x.Group).ThenBy(x => x.DisplayOrder)
                         .Where(x => x.Role == ElementRole.VisibleField))
                if (_fieldCache.TryGetValue(definition.Key, out var field)) Fields.Add(field);
        }
        else
        {
            var definitionsById = _snapshot.Elements.ToDictionary(x => x.Id);
            foreach (var id in selectedIds.Distinct())
                if (definitionsById.TryGetValue(id, out var definition)
                    && _fieldCache.TryGetValue(definition.Key, out var field)) Fields.Add(field);

            if (selectedIds.Count > 0 && Fields.Count == 0)
                foreach (var definition in _snapshot.Elements.OrderBy(x => x.Group).ThenBy(x => x.DisplayOrder)
                             .Where(x => x.Role == ElementRole.VisibleField))
                    if (_fieldCache.TryGetValue(definition.Key, out var field)) Fields.Add(field);
        }
        _hasConditionSelection = true;
        RefreshAvailableConditions();
        IsEditingConditions = false;
        RefreshConditionCommands();
        StatusSummary = $"当前配置 · {_snapshot.Rules.Count(x => x.IsEnabled)} 条启用规则";
        _isLoaded = true;
    }

    public async Task StartNewRunAsync()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        await LoadSnapshotIfChangedAsync();
        foreach (var field in _fieldCache.Values) field.ClearCopyFeedback();
        var manual = Fields.Where(x => x.IsManual && !string.IsNullOrWhiteSpace(x.InputText))
            .ToDictionary(x => x.Definition.Key, x => (object?)x.InputText, StringComparer.OrdinalIgnoreCase);
        _context = new RunContext(_snapshot, manual);
        _context.Changed += ContextOnChanged;
        IsRunning = true;
        RefreshFromContext();
        try { await _services.Scheduler.RunUntilStableAsync(_snapshot, _context, _runCancellation.Token); }
        finally
        {
            IsRunning = false;
            RefreshFromContext();
            await WriteRunDiagnosticAsync(_context);
        }
    }

    public async Task CommitFieldAndRunAsync(ElementFieldViewModel field)
    {
        field.CommitEditAsManual();
        await StartNewRunAsync();
    }

    public void CancelFieldEdit(ElementFieldViewModel field) { field.CancelEdit(); RefreshFromContext(); }
    public async Task CopyFieldAsync(ElementFieldViewModel field, string? selectedText = null)
    {
        var value = !string.IsNullOrEmpty(selectedText)
            ? selectedText
            : !string.IsNullOrEmpty(field.FullValue) ? field.FullValue : field.InputText;
        if (string.IsNullOrEmpty(value))
        {
            field.ClearCopyFeedback();
            return;
        }

        var feedbackVersion = field.BeginCopyFeedback();
        try
        {
            field.CompleteCopyFeedback(feedbackVersion, await ClipboardCopyService.TrySetTextAsync(value)
                ? "已复制"
                : "剪贴板忙，请重试");
        }
        catch
        {
            field.CompleteCopyFeedback(feedbackVersion, "复制失败，请重试");
        }
        _ = ClearCopyFeedbackLaterAsync(field, feedbackVersion);
    }

    private static async Task ClearCopyFeedbackLaterAsync(ElementFieldViewModel field, long feedbackVersion)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        field.ClearCopyFeedback(feedbackVersion);
    }

    private async Task SelectListRowAsync(Guid executionId, int index)
    {
        if (_context is null || index < 0 || !_context.SelectListRow(executionId, index)) return;
        IsRunning = true;
        try { await _services.Scheduler.RunUntilStableAsync(_snapshot, _context, _runCancellation?.Token ?? CancellationToken.None); }
        finally { IsRunning = false; RefreshFromContext(); }
    }

    private void ContextOnChanged(object? sender, EventArgs e)
    {
        if (Application.Current.Dispatcher.CheckAccess()) RefreshFromContext();
        else Application.Current.Dispatcher.BeginInvoke(RefreshFromContext);
    }

    private void RefreshFromContext()
    {
        if (_context is null) return;
        foreach (var field in Fields)
        {
            if (_context.Slots.TryGetValue(field.Definition.Key, out var slot))
            {
                var source = slot.Contributions.Select(x => x.RuleId)
                    .Where(x => x is not null).Select(x => DescribeRuleSourceObject(x!.Value))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
                field.UpdateFromSlot(slot, string.Join("、", source!));
            }
        }
        ListResults.Clear();
        foreach (var result in _context.ListResults)
            ListResults.Add(new ListResultViewModel(result, _snapshot, index => SelectListRowAsync(result.ExecutionId, index)));
        TraceItems.Clear();
        foreach (var item in _context.Executions) TraceItems.Add(new TraceItemViewModel(item));
        StatusSummary = BuildSummary(_context);
    }

    private async Task LoadSnapshotIfChangedAsync()
    {
        var current = await _services.Configurations.GetCurrentAsync() ?? new ConfigurationSnapshot();
        if (current.Id == _snapshot.Id && current.CreatedAt == _snapshot.CreatedAt) return;
        await LoadAsync();
    }

    private void Clear()
    {
        _runCancellation?.Cancel(); _context = null;
        foreach (var field in _fieldCache.Values) field.Clear();
        ListResults.Clear(); TraceItems.Clear(); StatusSummary = "已清空。输入字段后按 Enter 开始查询。";
    }

    public async Task ToggleConditionsEditAsync()
    {
        var isCompleting = IsEditingConditions;
        IsEditingConditions = !IsEditingConditions;
        if (!isCompleting) return;
        try
        {
            var ids = Fields.Select(x => x.Definition.Id).ToArray();
            await _services.Configurations.SetSettingAsync(ConditionLayoutSettingKey, JsonSerializer.Serialize(ids));
        }
        catch (Exception ex)
        {
            StatusSummary = "查询条件布局保存失败：" + ex.Message;
        }
    }

    private async Task<List<Guid>?> LoadConditionLayoutAsync()
    {
        try
        {
            var json = await _services.Configurations.GetSettingAsync(ConditionLayoutSettingKey);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch
        {
            return null;
        }
    }

    private void AddCondition(object? parameter)
    {
        if (parameter is not QueryConditionOptionViewModel option
            || !_fieldCache.TryGetValue(option.Definition.Key, out var field)
            || Fields.Any(x => x.Definition.Key.Equals(option.Definition.Key, StringComparison.OrdinalIgnoreCase))) return;

        Fields.Add(field);
        RefreshAvailableConditions();
        RefreshConditionCommands();
        if (_context?.Slots.TryGetValue(field.Definition.Key, out var slot) == true)
            field.UpdateFromSlot(slot, DescribeSlotSource(slot));
    }

    private void RemoveCondition(object? parameter)
    {
        if (parameter is not ElementFieldViewModel field || !Fields.Remove(field)) return;
        RefreshAvailableConditions();
        RefreshConditionCommands();
    }

    public void MoveCondition(ElementFieldViewModel dragged, ElementFieldViewModel? target)
    {
        if (!IsEditingConditions || IsRunning) return;
        var oldIndex = Fields.IndexOf(dragged);
        if (oldIndex < 0) return;
        var newIndex = target is null ? Fields.Count - 1 : Fields.IndexOf(target);
        if (newIndex < 0 || newIndex == oldIndex) return;
        Fields.Move(oldIndex, newIndex);
    }

    private void RefreshAvailableConditions()
    {
        var selectedKeys = Fields.Select(x => x.Definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AvailableConditions.Clear();
        foreach (var definition in _snapshot.Elements
                     .Where(x => !selectedKeys.Contains(x.Key))
                     .OrderBy(x => x.Group).ThenBy(x => x.DisplayOrder))
            AvailableConditions.Add(new QueryConditionOptionViewModel(definition));
    }

    private void RefreshConditionCommands()
    {
        ToggleConditionsEditCommand.RaiseCanExecuteChanged();
        AddConditionCommand.RaiseCanExecuteChanged();
        RemoveConditionCommand.RaiseCanExecuteChanged();
    }

    private string DescribeSlotSource(ElementSlot slot)
    {
        var source = slot.Contributions.Select(x => x.RuleId)
            .Where(x => x is not null).Select(x => DescribeRuleSourceObject(x!.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join("、", source!);
    }

    private string? DescribeRuleSourceObject(Guid ruleId)
    {
        var queryObject = _snapshot.QueryObjects.FirstOrDefault(item => item.Entries.Any(entry =>
            (entry.RuntimeRuleId == Guid.Empty ? entry.Id : entry.RuntimeRuleId) == ruleId));
        if (!string.IsNullOrWhiteSpace(queryObject?.Name)) return queryObject.Name;
        return _snapshot.Rules.FirstOrDefault(rule => rule.Id == ruleId)?.Name;
    }

    private string BuildSummary(RunContext context)
    {
        var success = context.Executions.Count(x => x.Status == RuleExecutionStatus.Success);
        var none = context.Executions.Count(x => x.Status == RuleExecutionStatus.NoResult);
        var failed = context.Executions.Count(x => x.Status == RuleExecutionStatus.Failed);
        var conflicts = context.Slots.Values.Count(x => x.State == ElementRuntimeState.Conflict);
        var status = context.Status switch
        {
            QueryRunStatus.Running => "正在查询",
            QueryRunStatus.WaitingForSelection => "等待选择一条记录",
            QueryRunStatus.Completed => "查询完成",
            QueryRunStatus.CompletedWithErrors => "查询完成，但存在错误",
            QueryRunStatus.Cancelled => "查询已取消",
            QueryRunStatus.LimitReached => "已触发 100 次规则熔断",
            _ => "准备就绪"
        };
        if (context.Executions.Count == 0 && context.Status == QueryRunStatus.Completed)
        {
            var readiness = BuildRuleReadiness(context);
            if (readiness.Count == 0)
                return "未执行任何查询入口 · 当前配置没有启用入口。请在 SQL 查询对象页配置并保存。";

            var compileErrors = readiness.Where(x => x.State == "SqlCompileError").ToArray();
            if (compileErrors.Length > 0)
                return "未执行任何规则 · 以下规则的 SQL 模板无法解析：" + string.Join("、", compileErrors.Select(x => x.RuleName)) + "。";

            var waiting = readiness.Where(x => x.State == "WaitingForInputs").ToArray();
            if (waiting.Length > 0)
            {
                var details = string.Join("；", waiting.Take(3).Select(x =>
                    $"“{x.RuleName}”等待 {string.Join("、", x.UnavailableElementKeys.Select(DescribeElement))}"));
                return $"未执行任何规则 · {details}。请确认 SQL 占位符使用元素的稳定 key，并给这些元素输入有效值。";
            }

            return "未执行任何规则 · 启用规则已满足输入但没有进入调度，请在“关于”页打开诊断日志，并将当天的 run 日志提供给开发人员。";
        }

        return $"{status} · 成功 {success} · 无结果 {none} · 失败 {failed} · 被阻断 {context.BlockedRuleCount} · 冲突 {conflicts}";
    }

    private IReadOnlyList<RuleReadinessMetadata> BuildRuleReadiness(RunContext context)
    {
        var result = new List<RuleReadinessMetadata>();
        foreach (var rule in _snapshot.Rules.Where(x => x.IsEnabled).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id))
        {
            CompiledSql compiled;
            try { compiled = SqlTemplateCompiler.Compile(rule.SqlTemplate); }
            catch
            {
                result.Add(new RuleReadinessMetadata(rule.Id, rule.Name, "SqlCompileError", [], []));
                continue;
            }

            var inputKeys = compiled.ParameterKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var unavailable = inputKeys.Where(key =>
                !context.Slots.TryGetValue(key, out var slot)
                || slot.EffectiveValue is null
                || slot.State is ElementRuntimeState.Empty or ElementRuntimeState.Conflict or ElementRuntimeState.Error).ToArray();
            var execution = context.Executions.LastOrDefault(x => x.RuleId == rule.Id);
            var state = execution is not null
                ? execution.Status.ToString()
                : unavailable.Length > 0 ? "WaitingForInputs" : "ReadyButNotExecuted";
            result.Add(new RuleReadinessMetadata(rule.Id, rule.Name, state, inputKeys, unavailable));
        }
        return result;
    }

    private async Task WriteRunDiagnosticAsync(RunContext context)
    {
        var metadata = new QueryRunMetadata(
            DateTimeOffset.UtcNow,
            context.RunId,
            _snapshot.Id,
            context.Status,
            _snapshot.Rules.Count(x => x.IsEnabled),
            context.GetManualValues().Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            context.Executions.Count,
            context.Executions.Count(x => x.Status == RuleExecutionStatus.Success),
            context.Executions.Count(x => x.Status == RuleExecutionStatus.NoResult),
            context.Executions.Count(x => x.Status == RuleExecutionStatus.Failed),
            context.BlockedRuleCount,
            context.Slots.Values.Count(x => x.State == ElementRuntimeState.Conflict),
            BuildRuleReadiness(context));
        try { await _services.RunDiagnostics.WriteRunAsync(metadata, CancellationToken.None); }
        catch { /* Diagnostics must never break a patient query. */ }
    }

    private string DescribeElement(string key)
    {
        var element = _snapshot.Elements.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return element is null ? key : $"{element.Label}（{element.Key}）";
    }

}

public sealed class QueryConditionOptionViewModel
{
    public ElementDefinition Definition { get; }
    public double DisplayWidth => Math.Max(160, Math.Round(Definition.Width * 4d / 7d));
    public string Label => Definition.Label;
    public string Key => Definition.Key;

    public QueryConditionOptionViewModel(ElementDefinition definition) => Definition = definition;
}

public sealed class ElementFieldViewModel : ObservableObject
{
    private string _inputText = string.Empty;
    private string _fullValue = string.Empty;
    private string _stateLabel = "可输入";
    private string _sourceLabel = string.Empty;
    private string _copyHint = string.Empty;
    private long _copyFeedbackVersion;
    private bool _isManual;
    private bool _isDerivedReadOnly;
    private bool _isEditing;
    private string _editBackup = string.Empty;
    private bool _suppressManualFlag;

    public ElementDefinition Definition { get; }
    public double DisplayWidth => Math.Max(160, Math.Round(Definition.Width * 4d / 7d));
    public string InputText { get => _inputText; set { if (Set(ref _inputText, value) && !_suppressManualFlag) IsManual = true; } }
    public string FullValue { get => _fullValue; private set => Set(ref _fullValue, value); }
    public string StateLabel { get => _stateLabel; private set => Set(ref _stateLabel, value); }
    public string SourceLabel { get => _sourceLabel; private set => Set(ref _sourceLabel, value); }
    public string CopyHint { get => _copyHint; set => Set(ref _copyHint, value); }
    public bool IsManual { get => _isManual; private set => Set(ref _isManual, value); }
    public bool IsDerivedReadOnly { get => _isDerivedReadOnly; private set { if (Set(ref _isDerivedReadOnly, value)) Raise(nameof(IsEditorVisible)); } }
    public bool IsEditing { get => _isEditing; private set { if (Set(ref _isEditing, value)) Raise(nameof(IsEditorVisible)); } }
    public bool IsEditorVisible => IsEditing || (Definition.CanInput && !IsDerivedReadOnly);

    public ElementFieldViewModel(ElementDefinition definition) => Definition = definition;
    public void SetInitialManual(string value) { _suppressManualFlag = true; InputText = value; _suppressManualFlag = false; IsManual = true; }
    public void BeginEdit() { if (!IsDerivedReadOnly) return; _editBackup = InputText; IsEditing = true; ClearCopyFeedback(); }
    public void CommitEditAsManual() { IsEditing = false; IsDerivedReadOnly = false; IsManual = true; StateLabel = "人工输入"; }
    public void CancelEdit() { _suppressManualFlag = true; InputText = _editBackup; _suppressManualFlag = false; IsEditing = false; }

    public void UpdateFromSlot(ElementSlot slot, string source)
    {
        var display = slot.State == ElementRuntimeState.Conflict
            ? string.Join(" / ", slot.Contributions.Select(x => Format(x.Value)).Distinct())
            : slot.EffectiveValue is null ? string.Empty : Format(slot.EffectiveValue);
        ClearCopyFeedback();
        _suppressManualFlag = true; InputText = display; _suppressManualFlag = false;
        FullValue = display; SourceLabel = source;
        IsManual = slot.State == ElementRuntimeState.Manual;
        IsDerivedReadOnly = slot.State is ElementRuntimeState.Derived or ElementRuntimeState.Conflict;
        StateLabel = slot.State switch
        {
            ElementRuntimeState.Manual => "人工输入",
            ElementRuntimeState.Derived => "SQL 回填",
            ElementRuntimeState.Conflict => "值冲突",
            ElementRuntimeState.Error => "格式错误",
            _ => Definition.CanInput ? "可输入" : "等待结果"
        };
    }

    public void Clear()
    {
        ClearCopyFeedback();
        _suppressManualFlag = true; InputText = string.Empty; _suppressManualFlag = false;
        FullValue = string.Empty; IsManual = false; IsDerivedReadOnly = false; IsEditing = false; StateLabel = Definition.CanInput ? "可输入" : "等待结果"; SourceLabel = string.Empty;
    }

    internal long BeginCopyFeedback()
    {
        var version = ++_copyFeedbackVersion;
        CopyHint = string.Empty;
        return version;
    }

    internal void CompleteCopyFeedback(long version, string message)
    {
        if (_copyFeedbackVersion == version) CopyHint = message;
    }

    internal void ClearCopyFeedback()
    {
        _copyFeedbackVersion++;
        CopyHint = string.Empty;
    }

    internal void ClearCopyFeedback(long version)
    {
        if (_copyFeedbackVersion != version) return;
        _copyFeedbackVersion++;
        CopyHint = string.Empty;
    }

    private string Format(object value)
    {
        if (Definition.EffectiveDataType == ElementDataType.Date)
        {
            try { return ElementValueConverter.FormatDate(value); }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
            { return value.ToString() ?? string.Empty; }
        }
        return value switch
        {
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "是" : "否",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}

public sealed class ListResultViewModel : ObservableObject
{
    private readonly Func<int, Task> _select;
    private int _selectedIndex;
    private bool _initializing = true;
    public Guid ExecutionId { get; }
    public string RuleName { get; }
    public DataView Rows { get; }
    public string SelectionHint { get; }
    public int SelectedIndex
    {
        get => _selectedIndex;
        set { if (!Set(ref _selectedIndex, value) || _initializing || value < 0) return; _ = _select(value); }
    }

    public ListResultViewModel(ListResultState state, ConfigurationSnapshot snapshot, Func<int, Task> select)
    {
        _select = select; ExecutionId = state.ExecutionId; RuleName = state.RuleName;
        Rows = BuildTable(state.Rows, state.Mappings, snapshot).DefaultView; _selectedIndex = state.SelectedIndex ?? -1;
        SelectionHint = state.Rows.Count == 1 ? "唯一记录已自动选中" : $"请选择 1 条记录后继续（共 {state.Rows.Count} 条）";
        _initializing = false;
    }

    private static DataTable BuildTable(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<OutputMapping> mappings, ConfigurationSnapshot snapshot)
    {
        var table = new DataTable();
        var sourceKeys = rows.SelectMany(x => x.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappedElements = new Dictionary<string, ElementDefinition?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in sourceKeys)
        {
            var mapping = mappings.FirstOrDefault(x => x.ColumnName.Equals(key, StringComparison.OrdinalIgnoreCase));
            var element = mapping is null ? null : snapshot.Elements.FirstOrDefault(x => x.Key.Equals(mapping.ElementKey, StringComparison.OrdinalIgnoreCase));
            var label = element?.Label ?? key;
            var unique = label; var suffix = 2; while (table.Columns.Contains(unique)) unique = label + " " + suffix++;
            table.Columns.Add(unique); displayNames[key] = unique; mappedElements[key] = element;
        }
        foreach (var source in rows)
        {
            var row = table.NewRow();
            foreach (var pair in source)
            {
                if (pair.Value is null) { row[displayNames[pair.Key]] = DBNull.Value; continue; }
                var element = mappedElements[pair.Key];
                row[displayNames[pair.Key]] = element?.EffectiveDataType == ElementDataType.Date
                    ? FormatDateForDisplay(pair.Value)
                    : pair.Value;
            }
            table.Rows.Add(row);
        }
        return table;
    }

    private static object FormatDateForDisplay(object value)
    {
        try { return ElementValueConverter.FormatDate(value); }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException) { return value; }
    }
}

public sealed class TraceItemViewModel
{
    public string RuleName { get; }
    public string Status { get; }
    public string Detail { get; }
    public TraceItemViewModel(RuleExecutionRecord record)
    {
        RuleName = record.RuleName; Status = record.Status.ToString();
        Detail = $"{record.RowCount} 行 · {record.Duration.TotalMilliseconds:F0} ms" + (string.IsNullOrWhiteSpace(record.Message) ? string.Empty : " · " + record.Message);
    }
}
