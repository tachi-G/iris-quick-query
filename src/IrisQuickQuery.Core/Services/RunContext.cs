using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public sealed class RunContext
{
    private readonly Dictionary<string, ElementSlot> _slots;

    public Guid RunId { get; } = Guid.NewGuid();
    public QueryRunStatus Status { get; internal set; } = QueryRunStatus.Ready;
    public int ExecutedRuleCount { get; internal set; }
    public int BlockedRuleCount { get; internal set; }
    public IReadOnlyDictionary<string, ElementSlot> Slots => _slots;
    public List<RuleExecutionRecord> Executions { get; } = [];
    public List<ListResultState> ListResults { get; } = [];
    public event EventHandler? Changed;

    public RunContext(ConfigurationSnapshot snapshot, IReadOnlyDictionary<string, object?>? manualValues = null)
    {
        _slots = snapshot.Elements.ToDictionary(x => x.Key, x => new ElementSlot(x.Key), StringComparer.OrdinalIgnoreCase);
        if (manualValues is null) return;
        foreach (var pair in manualValues) SetManual(snapshot, pair.Key, pair.Value);
    }

    public bool SetManual(ConfigurationSnapshot snapshot, string elementKey, object? rawValue)
    {
        if (!_slots.TryGetValue(elementKey, out var slot)) return false;
        var definition = snapshot.Elements.First(x => x.Key.Equals(elementKey, StringComparison.OrdinalIgnoreCase));
        var token = $"manual:{elementKey}";
        slot.RemoveWhere(x => x.SourceToken == token);
        if (rawValue is null || (rawValue is string text && string.IsNullOrWhiteSpace(text)))
        {
            OnChanged();
            return true;
        }
        if (!ElementValueConverter.TryConvert(rawValue, definition, out var value, out var error) || value is null)
        {
            slot.SetError(error ?? "输入值无效。");
            OnChanged();
            return false;
        }
        slot.AddOrReplace(new ValueContribution(token, value, true, null, new HashSet<string>(StringComparer.Ordinal) { token }));
        OnChanged();
        return true;
    }

    public IReadOnlyDictionary<string, object?> GetManualValues()
        => _slots.Values.SelectMany(x => x.Contributions)
            .Where(x => x.IsManual)
            .GroupBy(x => x.SourceToken, StringComparer.Ordinal)
            .ToDictionary(x => x.Key["manual:".Length..], x => (object?)x.Last().Value, StringComparer.OrdinalIgnoreCase);

    public bool HasExecuted(Guid ruleId, string fingerprint)
        => Executions.Any(x => x.RuleId == ruleId && x.InputFingerprint == fingerprint && x.Status != RuleExecutionStatus.Cancelled);

    public bool TryGetReadyInputs(QueryRuleDefinition rule, CompiledSql compiled, out IReadOnlyList<QueryParameter> parameters,
        out string fingerprint, out HashSet<string> dependencyTokens)
    {
        var values = new List<QueryParameter>(compiled.ParameterKeys.Count);
        var fingerprintParts = new List<string>();
        dependencyTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in compiled.ParameterKeys)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.EffectiveValue is null
                || slot.State is ElementRuntimeState.Empty or ElementRuntimeState.Conflict or ElementRuntimeState.Error)
            {
                parameters = [];
                fingerprint = string.Empty;
                return false;
            }
            var contribution = slot.Contributions.First(x => ValueComparer.AreEqual(x.Value, slot.EffectiveValue));
            foreach (var token in contribution.DependencyTokens) dependencyTokens.Add(token);
            var definition = CurrentSnapshot.Elements.First(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            values.Add(new QueryParameter(key, definition.EffectiveDataType, slot.EffectiveValue));
            fingerprintParts.Add($"{key}={ValueComparer.Canonical(slot.EffectiveValue)}");
        }
        parameters = values;
        fingerprint = string.Join("\u001f", fingerprintParts);
        return true;
    }

    internal ConfigurationSnapshot CurrentSnapshot { get; set; } = null!;

    internal bool AddMappedRow(QueryRuleDefinition rule, RuleExecutionRecord execution,
        IReadOnlyDictionary<string, object?> row, string sourceToken, IReadOnlySet<string> upstreamTokens)
    {
        var changed = false;
        foreach (var mapping in rule.OutputMappings)
        {
            if (!TryGetColumn(row, mapping.ColumnName, out var raw) || raw is null) continue;
            if (!_slots.TryGetValue(mapping.ElementKey, out var slot)) continue;
            var definition = CurrentSnapshot.Elements.First(x => x.Key.Equals(mapping.ElementKey, StringComparison.OrdinalIgnoreCase));
            if (!ElementValueConverter.TryConvert(raw, definition, out var converted, out var error) || converted is null)
            {
                slot.SetError(error ?? $"规则 {rule.Name} 输出转换失败。");
                execution.Status = RuleExecutionStatus.Failed;
                execution.ErrorCategory = "type-conversion";
                execution.Message = error;
                changed = true;
                continue;
            }
            var dependencies = new HashSet<string>(upstreamTokens, StringComparer.Ordinal) { sourceToken };
            changed |= slot.AddOrReplace(new ValueContribution(sourceToken + ":" + mapping.ElementKey, converted, false, rule.Id, dependencies));
        }
        if (changed) OnChanged();
        return changed;
    }

    public bool SelectListRow(Guid executionId, int rowIndex)
    {
        var list = ListResults.FirstOrDefault(x => x.ExecutionId == executionId);
        if (list is null || rowIndex < 0 || rowIndex >= list.Rows.Count) return false;
        if (list.SelectionToken is not null) InvalidateToken(list.SelectionToken);

        var execution = Executions.First(x => x.ExecutionId == executionId);
        var rule = CurrentSnapshot.Rules.First(x => x.Id == list.RuleId);
        var token = $"selection:{executionId:N}:{rowIndex}:{Guid.NewGuid():N}";
        list.SelectedIndex = rowIndex;
        list.SelectionToken = token;
        AddMappedRow(rule, execution, list.Rows[rowIndex], token, execution.InputDependencyTokens);
        execution.Status = RuleExecutionStatus.Success;
        OnChanged();
        return true;
    }

    private void InvalidateToken(string token)
    {
        foreach (var slot in _slots.Values)
            slot.RemoveWhere(x => x.DependencyTokens.Contains(token));
        var invalidExecutions = Executions.Where(x => x.InputDependencyTokens.Contains(token)).ToArray();
        foreach (var execution in invalidExecutions)
        {
            foreach (var slot in _slots.Values) slot.RemoveWhere(x => x.SourceToken.StartsWith(execution.SourceToken, StringComparison.Ordinal));
            ListResults.RemoveAll(x => x.ExecutionId == execution.ExecutionId);
            Executions.Remove(execution);
        }
    }

    internal void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static bool TryGetColumn(IReadOnlyDictionary<string, object?> row, string name, out object? value)
    {
        if (row.TryGetValue(name, out value)) return true;
        var match = row.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        value = match.Value;
        return match.Key is not null;
    }
}
