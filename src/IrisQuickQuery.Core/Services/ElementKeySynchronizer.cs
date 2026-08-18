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
}
