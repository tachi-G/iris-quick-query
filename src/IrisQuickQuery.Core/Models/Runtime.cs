using System.Globalization;

namespace IrisQuickQuery.Core.Models;

public enum ElementRuntimeState { Empty, Manual, Derived, Conflict, Error }
public enum RuleExecutionStatus { Running, Success, NoResult, WaitingForSelection, Failed, Blocked, Cancelled }
public enum QueryRunStatus { Ready, Running, WaitingForSelection, Completed, CompletedWithErrors, Cancelled, LimitReached }

public sealed record ValueContribution(
    string SourceToken,
    object Value,
    bool IsManual,
    Guid? RuleId,
    IReadOnlySet<string> DependencyTokens);

public sealed class ElementSlot
{
    private readonly Dictionary<string, ValueContribution> _contributions = new(StringComparer.Ordinal);

    public string ElementKey { get; }
    public long Revision { get; private set; }
    public IReadOnlyCollection<ValueContribution> Contributions => _contributions.Values;
    public ElementRuntimeState State { get; private set; } = ElementRuntimeState.Empty;
    public object? EffectiveValue { get; private set; }
    public string? ErrorMessage { get; private set; }

    public ElementSlot(string elementKey) => ElementKey = elementKey;

    public bool AddOrReplace(ValueContribution contribution)
    {
        var changed = !_contributions.TryGetValue(contribution.SourceToken, out var old)
            || !ValueComparer.AreEqual(old.Value, contribution.Value)
            || !old.DependencyTokens.SetEquals(contribution.DependencyTokens);
        if (!changed) return false;
        _contributions[contribution.SourceToken] = contribution;
        Recalculate();
        return true;
    }

    public bool RemoveWhere(Func<ValueContribution, bool> predicate)
    {
        var keys = _contributions.Where(x => predicate(x.Value)).Select(x => x.Key).ToArray();
        foreach (var key in keys) _contributions.Remove(key);
        if (keys.Length == 0) return false;
        Recalculate();
        return true;
    }

    public void SetError(string message)
    {
        ErrorMessage = message;
        State = ElementRuntimeState.Error;
        Revision++;
    }

    private void Recalculate()
    {
        ErrorMessage = null;
        var values = _contributions.Values.Select(x => x.Value).Distinct(ValueComparer.Instance).ToArray();
        EffectiveValue = values.FirstOrDefault();
        State = values.Length switch
        {
            0 => ElementRuntimeState.Empty,
            > 1 => ElementRuntimeState.Conflict,
            _ when _contributions.Values.Any(x => x.IsManual) => ElementRuntimeState.Manual,
            _ => ElementRuntimeState.Derived
        };
        Revision++;
    }
}

public sealed class RuleExecutionRecord
{
    public Guid ExecutionId { get; init; } = Guid.NewGuid();
    public Guid RuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public string InputFingerprint { get; init; } = string.Empty;
    public string SourceToken => $"rule:{ExecutionId:N}";
    public HashSet<string> InputDependencyTokens { get; init; } = new(StringComparer.Ordinal);
    public RuleExecutionStatus Status { get; set; }
    public int RowCount { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorCategory { get; set; }
    public string? Message { get; set; }
}

public sealed class ListResultState
{
    public Guid ExecutionId { get; init; }
    public Guid RuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; } = [];
    public IReadOnlyList<OutputMapping> Mappings { get; init; } = [];
    public int? SelectedIndex { get; set; }
    public string? SelectionToken { get; set; }
}

public static class ValueComparer
{
    public static readonly IEqualityComparer<object> Instance = new ObjectValueComparer();

    public static bool AreEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left is DateTime ldt && right is DateTime rdt) return ldt == rdt;
        if (left is DateOnly ld && right is DateOnly rd) return ld == rd;
        if (IsNumber(left) && IsNumber(right))
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture);
        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public static string Canonical(object value) => value switch
    {
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private sealed class ObjectValueComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => AreEqual(x, y);
        public int GetHashCode(object obj) => Canonical(obj).GetHashCode(StringComparison.Ordinal);
    }
}

internal static class SetExtensions
{
    public static bool SetEquals(this IReadOnlySet<string> left, IReadOnlySet<string> right)
        => left.Count == right.Count && left.All(right.Contains);
}
