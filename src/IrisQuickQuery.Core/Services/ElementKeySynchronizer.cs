using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public sealed record ElementKeyChange(Guid ElementId, string OldKey, string NewKey);

public static class ElementKeySynchronizer
{
    public static IReadOnlyList<ElementKeyChange> Detect(
        IEnumerable<ElementDefinition> sourceElements,
        IEnumerable<ElementDefinition> currentElements)
    {
        var sourceById = sourceElements.Where(x => x.Id != Guid.Empty).ToDictionary(x => x.Id);
        return currentElements
            .Where(x => x.Id != Guid.Empty && sourceById.TryGetValue(x.Id, out var source)
                        && !string.Equals(source.Key, x.Key, StringComparison.Ordinal))
            .Select(x => new ElementKeyChange(x.Id, sourceById[x.Id].Key, x.Key))
            .ToArray();
    }

    public static int Apply(IEnumerable<QueryRuleDefinition> rules, IEnumerable<ElementKeyChange> changes)
    {
        var renameMap = changes
            .Where(x => !string.IsNullOrWhiteSpace(x.OldKey) && !string.IsNullOrWhiteSpace(x.NewKey))
            .ToDictionary(x => x.OldKey, x => x.NewKey, StringComparer.OrdinalIgnoreCase);
        if (renameMap.Count == 0) return 0;

        var updatedRules = 0;
        foreach (var rule in rules)
        {
            var changed = false;
            var updatedSql = SqlTemplateCompiler.RenameElementKeys(rule.SqlTemplate ?? string.Empty, renameMap);
            if (!string.Equals(updatedSql, rule.SqlTemplate, StringComparison.Ordinal))
            {
                rule.SqlTemplate = updatedSql;
                changed = true;
            }

            foreach (var mapping in rule.OutputMappings)
            {
                if (!renameMap.TryGetValue(mapping.ElementKey ?? string.Empty, out var replacement)) continue;
                mapping.ElementKey = replacement;
                changed = true;
            }
            if (changed) updatedRules++;
        }
        return updatedRules;
    }

    public static int Apply(IEnumerable<QueryObjectDefinition> queryObjects, IEnumerable<ElementKeyChange> changes)
    {
        var renameMap = changes
            .Where(x => !string.IsNullOrWhiteSpace(x.OldKey) && !string.IsNullOrWhiteSpace(x.NewKey))
            .ToDictionary(x => x.OldKey, x => x.NewKey, StringComparer.OrdinalIgnoreCase);
        if (renameMap.Count == 0) return 0;

        var updatedObjects = 0;
        foreach (var queryObject in queryObjects)
        {
            var changed = false;
            var updatedBase = SqlTemplateCompiler.RenameElementKeys(queryObject.BaseSqlTemplate ?? string.Empty, renameMap);
            if (!string.Equals(updatedBase, queryObject.BaseSqlTemplate, StringComparison.Ordinal))
            {
                queryObject.BaseSqlTemplate = updatedBase;
                changed = true;
            }
            foreach (var entry in queryObject.Entries)
            {
                var updatedFilter = SqlTemplateCompiler.RenameElementKeys(entry.FilterTemplate ?? string.Empty, renameMap);
                var updatedOrder = SqlTemplateCompiler.RenameElementKeys(entry.OrderByTemplate ?? string.Empty, renameMap);
                var updatedOverride = string.IsNullOrWhiteSpace(entry.SqlTemplateOverride)
                    ? entry.SqlTemplateOverride
                    : SqlTemplateCompiler.RenameElementKeys(entry.SqlTemplateOverride, renameMap);
                if (!string.Equals(updatedFilter, entry.FilterTemplate, StringComparison.Ordinal)) { entry.FilterTemplate = updatedFilter; changed = true; }
                if (!string.Equals(updatedOrder, entry.OrderByTemplate, StringComparison.Ordinal)) { entry.OrderByTemplate = updatedOrder; changed = true; }
                if (!string.Equals(updatedOverride, entry.SqlTemplateOverride, StringComparison.Ordinal)) { entry.SqlTemplateOverride = updatedOverride; changed = true; }
            }
            foreach (var mapping in queryObject.OutputMappings)
            {
                if (!renameMap.TryGetValue(mapping.ElementKey ?? string.Empty, out var replacement)) continue;
                mapping.ElementKey = replacement;
                changed = true;
            }
            if (changed) updatedObjects++;
        }
        return updatedObjects;
    }
}
