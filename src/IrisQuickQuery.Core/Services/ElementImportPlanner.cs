using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public sealed record ElementImportPlan(
    IReadOnlyList<ElementDefinition> MergedElements,
    int AddedCount,
    int UpdatedCount,
    int ReusedCount,
    int UntouchedLocalCount,
    IReadOnlyList<string> Conflicts)
{
    public bool CanApply => Conflicts.Count == 0;
}

public static class ElementImportPlanner
{
    public static ElementImportPlan Create(IReadOnlyCollection<ElementDefinition> localElements,
        IReadOnlyCollection<ElementDefinition> importedElements)
    {
        var conflicts = new List<string>();
        ValidateUnique(localElements, "本机", conflicts);
        ValidateUnique(importedElements, "规则包", conflicts);
        if (conflicts.Count > 0)
            return new ElementImportPlan(localElements.Select(Clone).ToArray(), 0, 0, 0, localElements.Count, conflicts);

        var merged = localElements.Select(Clone).ToList();
        var byId = merged.ToDictionary(x => x.Id);
        var byKey = merged.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var touchedIds = new HashSet<Guid>();
        var added = 0;
        var updated = 0;
        var reused = 0;

        foreach (var incoming in importedElements)
        {
            byId.TryGetValue(incoming.Id, out var idMatch);
            byKey.TryGetValue(incoming.Key, out var keyMatch);
            if (idMatch is not null && keyMatch is not null && !ReferenceEquals(idMatch, keyMatch))
            {
                conflicts.Add($"规则包元素“{incoming.Label}（{incoming.Key}）”的固定身份与本机另一个同名元素冲突。");
                continue;
            }

            var target = idMatch ?? keyMatch;
            if (target is null)
            {
                var addition = Clone(incoming);
                if (addition.Id == Guid.Empty) addition.Id = Guid.NewGuid();
                merged.Add(addition);
                byId[addition.Id] = addition;
                byKey[addition.Key] = addition;
                touchedIds.Add(addition.Id);
                added++;
                continue;
            }

            var unchanged = EquivalentDefinition(target, incoming);
            var retainedId = target.Id;
            var previousKey = target.Key;
            CopyDefinition(incoming, target);
            target.Id = retainedId;
            if (!string.Equals(previousKey, target.Key, StringComparison.OrdinalIgnoreCase)) byKey.Remove(previousKey);
            byKey[target.Key] = target;
            touchedIds.Add(target.Id);
            if (unchanged) reused++;
            else updated++;
        }

        if (conflicts.Count > 0)
            return new ElementImportPlan(localElements.Select(Clone).ToArray(), 0, 0, 0, localElements.Count, conflicts);
        return new ElementImportPlan(merged, added, updated, reused,
            localElements.Count(x => !touchedIds.Contains(x.Id)), conflicts);
    }

    private static void ValidateUnique(IEnumerable<ElementDefinition> elements, string source, ICollection<string> conflicts)
    {
        foreach (var duplicate in elements.GroupBy(x => x.Id).Where(x => x.Key != Guid.Empty && x.Count() > 1))
            conflicts.Add($"{source}存在重复的全局元素固定身份：{duplicate.Key:D}。");
        foreach (var duplicate in elements.Where(x => !string.IsNullOrWhiteSpace(x.Key))
                     .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            conflicts.Add($"{source}存在重复的全局元素名称：{duplicate.Key}。");
    }

    private static bool EquivalentDefinition(ElementDefinition left, ElementDefinition right)
        => string.Equals(left.Key, right.Key, StringComparison.Ordinal)
           && string.Equals(left.Label, right.Label, StringComparison.Ordinal)
           && left.DataType == right.DataType
           && left.IsDate == right.IsDate
           && left.Role == right.Role
           && string.Equals(left.Group, right.Group, StringComparison.Ordinal)
           && left.DisplayOrder == right.DisplayOrder
           && left.Width == right.Width
           && left.CanInput == right.CanInput
           && left.IsSensitive == right.IsSensitive
           && string.Equals(left.ValidationPattern, right.ValidationPattern, StringComparison.Ordinal);

    private static void CopyDefinition(ElementDefinition source, ElementDefinition target)
    {
        target.Key = source.Key;
        target.Label = source.Label;
        target.DataType = source.DataType;
        target.IsDate = source.IsDate;
        target.Role = source.Role;
        target.Group = source.Group;
        target.DisplayOrder = source.DisplayOrder;
        target.Width = source.Width;
        target.CanInput = source.CanInput;
        target.IsSensitive = source.IsSensitive;
        target.ValidationPattern = source.ValidationPattern;
    }

    private static ElementDefinition Clone(ElementDefinition source)
    {
        var clone = new ElementDefinition { Id = source.Id };
        CopyDefinition(source, clone);
        return clone;
    }
}
