using System.Text;
using System.Text.RegularExpressions;
using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public sealed record QueryObjectMigrationResult(
    IReadOnlyList<QueryObjectDefinition> QueryObjects,
    int SourceRuleCount,
    int GroupedObjectCount,
    int SharedRuleReduction);

public static partial class QueryObjectCompiler
{
    public static IReadOnlyList<QueryRuleDefinition> Compile(IEnumerable<QueryObjectDefinition> queryObjects)
    {
        var rules = new List<QueryRuleDefinition>();
        foreach (var queryObject in queryObjects.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id))
        {
            foreach (var entry in queryObject.Entries.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id))
            {
                rules.Add(new QueryRuleDefinition
                {
                    Id = entry.RuntimeRuleId == Guid.Empty ? entry.Id : entry.RuntimeRuleId,
                    Name = NormalizeEntryName(entry.Name),
                    SqlTemplate = ComposeSql(queryObject, entry),
                    IsEnabled = queryObject.IsEnabled && entry.IsEnabled,
                    ResultMode = entry.ResultMode,
                    OutputMappings = queryObject.OutputMappings.Select(CloneMapping).ToList(),
                    DisplayOrder = queryObject.DisplayOrder * 1000 + entry.DisplayOrder,
                    TimeoutSeconds = entry.TimeoutSeconds,
                    MaxRows = entry.MaxRows
                });
            }
        }
        return rules;
    }

    public static void SynchronizeExecutableRules(ConfigurationSnapshot snapshot)
    {
        if (snapshot.QueryObjects.Count == 0) return;
        snapshot.Rules = Compile(snapshot.QueryObjects).ToList();
        snapshot.SchemaVersion = ConfigurationSnapshot.CurrentSchemaVersion;
    }

    public static string ComposeSql(QueryObjectDefinition queryObject, QueryEntryDefinition entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.SqlTemplateOverride)) return entry.SqlTemplateOverride.Trim();
        var sql = queryObject.BaseSqlTemplate.Trim();
        if (entry.TakeFirst) sql = InsertTopOne(sql);
        if (!string.IsNullOrWhiteSpace(entry.FilterTemplate)) sql += "\nWHERE " + entry.FilterTemplate.Trim();
        if (!string.IsNullOrWhiteSpace(entry.OrderByTemplate)) sql += "\nORDER BY " + entry.OrderByTemplate.Trim();
        return sql;
    }

    public static QueryObjectMigrationResult MigrateRules(IEnumerable<QueryRuleDefinition> sourceRules)
    {
        var rules = sourceRules.Select(CloneRule).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id).ToArray();
        var parsed = rules.Select(rule => new ParsedCandidate(rule, TryParseSimpleRule(rule))).ToArray();
        var objects = new List<QueryObjectDefinition>();

        foreach (var sourceGroup in parsed.GroupBy(x => x.Parsed?.SourceKey ?? "legacy:" + x.Rule.Id))
        {
            var candidates = sourceGroup.ToArray();
            if (candidates.Any(x => x.Parsed is null) || !TryBuildSharedObject(candidates, objects.Count, out var queryObject))
            {
                foreach (var candidate in candidates) objects.Add(CreateLegacyObject(candidate.Rule, objects.Count));
                continue;
            }
            objects.Add(queryObject!);
        }

        var ordered = objects.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id).ToArray();
        return new QueryObjectMigrationResult(ordered, rules.Length, ordered.Length, Math.Max(0, rules.Length - ordered.Length));
    }

    private static bool TryBuildSharedObject(IReadOnlyList<ParsedCandidate> candidates, int objectIndex,
        out QueryObjectDefinition? queryObject)
    {
        queryObject = null;
        var parsed = candidates.Select(x => x.Parsed!).ToArray();
        var expressions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappingsByElement = new Dictionary<string, OutputMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parsed)
        {
            foreach (var pair in item.SelectExpressions)
            {
                if (expressions.TryGetValue(pair.Key, out var existing)
                    && !NormalizeSql(existing).Equals(NormalizeSql(pair.Value), StringComparison.OrdinalIgnoreCase)) return false;
                expressions[pair.Key] = pair.Value;
            }
            foreach (var mapping in item.Rule.OutputMappings)
            {
                if (mappingsByElement.TryGetValue(mapping.ElementKey, out var existing)
                    && !string.Equals(existing.ColumnName, mapping.ColumnName, StringComparison.OrdinalIgnoreCase)) return false;
                mappingsByElement[mapping.ElementKey] = CloneMapping(mapping);
            }
        }

        var first = parsed[0];
        var selectedExpressions = new List<string>();
        var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parsed)
        foreach (var pair in item.SelectExpressions)
            if (selectedNames.Add(pair.Key)) selectedExpressions.Add(pair.Value);

        var displayOrder = candidates.Min(x => x.Rule.DisplayOrder);
        queryObject = new QueryObjectDefinition
        {
            Name = SuggestObjectName(first.FromClause, candidates.Select(x => x.Rule.Name)),
            BaseSqlTemplate = "SELECT " + string.Join(", ", selectedExpressions) + "\nFROM " + first.FromClause.Trim(),
            DisplayOrder = displayOrder == 0 ? objectIndex * 10 + 10 : displayOrder,
            OutputMappings = mappingsByElement.Values.ToList(),
            Entries = parsed.Select((item, index) => new QueryEntryDefinition
            {
                RuntimeRuleId = item.Rule.Id,
                Name = NormalizeEntryName(item.Rule.Name),
                FilterTemplate = item.FilterClause,
                OrderByTemplate = item.OrderByClause,
                TakeFirst = item.TakeFirst,
                IsEnabled = item.Rule.IsEnabled,
                ResultMode = item.Rule.ResultMode,
                DisplayOrder = item.Rule.DisplayOrder == 0 ? index * 10 + 10 : item.Rule.DisplayOrder,
                TimeoutSeconds = item.Rule.TimeoutSeconds,
                MaxRows = item.Rule.MaxRows
            }).ToList()
        };
        return true;
    }

    private static QueryObjectDefinition CreateLegacyObject(QueryRuleDefinition rule, int index) => new()
    {
        Name = NormalizeEntryName(rule.Name),
        BaseSqlTemplate = rule.SqlTemplate,
        DisplayOrder = rule.DisplayOrder == 0 ? index * 10 + 10 : rule.DisplayOrder,
        OutputMappings = rule.OutputMappings.Select(CloneMapping).ToList(),
        Entries =
        [
            new QueryEntryDefinition
            {
                RuntimeRuleId = rule.Id,
                Name = NormalizeEntryName(rule.Name),
                SqlTemplateOverride = rule.SqlTemplate,
                IsEnabled = rule.IsEnabled,
                ResultMode = rule.ResultMode,
                DisplayOrder = 10,
                TimeoutSeconds = rule.TimeoutSeconds,
                MaxRows = rule.MaxRows
            }
        ]
    };

    private static ParsedSimpleRule? TryParseSimpleRule(QueryRuleDefinition rule)
    {
        var sql = (rule.SqlTemplate ?? string.Empty).Trim();
        var match = SimpleSelectRegex().Match(sql);
        if (!match.Success) return null;
        var select = match.Groups["select"].Value.Trim();
        var takeFirst = TopOneRegex().IsMatch(select);
        if (takeFirst) select = TopOneRegex().Replace(select, string.Empty, 1).TrimStart();
        if (select.StartsWith("DISTINCT ", StringComparison.OrdinalIgnoreCase)) return null;
        var expressions = SplitTopLevelComma(select);
        var resultNames = SqlSelectListParser.GetResultColumnNames(sql);
        if (expressions.Count == 0 || expressions.Count != resultNames.Count) return null;
        var expressionMap = resultNames.Select((name, index) => new KeyValuePair<string, string>(name, expressions[index])).ToArray();
        var from = match.Groups["from"].Value.Trim();
        var filter = match.Groups["where"].Value.Trim();
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(filter)) return null;
        return new ParsedSimpleRule(rule, from, filter, match.Groups["order"].Value.Trim(), takeFirst,
            expressionMap, NormalizeSql(from), GetSourceDisplayName(from));
    }

    private static List<string> SplitTopLevelComma(string value)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    if (index + 1 < value.Length && value[index + 1] == quote) index++;
                    else quote = '\0';
                }
                continue;
            }
            if (ch is '\'' or '"' or '`') { quote = ch; continue; }
            if (ch == '[') { quote = ']'; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (ch != ',' || depth != 0) continue;
            result.Add(value[start..index].Trim());
            start = index + 1;
        }
        result.Add(value[start..].Trim());
        return result.Where(x => x.Length > 0).ToList();
    }

    private static string InsertTopOne(string sql)
    {
        var match = LeadingSelectRegex().Match(sql);
        if (!match.Success) return sql;
        return sql.Insert(match.Length, "TOP 1 ");
    }

    private static string GetSourceDisplayName(string fromClause)
    {
        var token = fromClause.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "查询对象";
        return token.Trim('[', ']', '"') + " 查询对象";
    }

    private static string SuggestObjectName(string fromClause, IEnumerable<string> ruleNames)
    {
        var source = NormalizeSql(fromClause).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var knownName = source.Trim('[', ']', '"').ToUpperInvariant() switch
        {
            "PA_PATMAS" => "患者基本信息",
            "PA_ADM" => "就诊信息",
            "MA_IPMR_SS.MEDICARENO" => "病案号与就诊号",
            "CT_CAREPROV" => "医生信息",
            "CT_LOC" => "科室信息",
            _ => string.Empty
        };
        if (knownName.Length > 0) return knownName;
        var names = ruleNames.Select(NormalizeEntryName).Where(x => x.Length > 0).Distinct().ToArray();
        return names.Length == 1 ? names[0] : GetSourceDisplayName(fromClause);
    }

    private static string NormalizeEntryName(string name)
        => string.Equals(name?.Trim(), "根据病案号取用户ID", StringComparison.Ordinal)
            ? "根据病案号取就诊号"
            : name?.Trim() ?? string.Empty;

    private static string NormalizeSql(string value)
        => WhitespaceRegex().Replace(value.Trim(), " ");

    private static OutputMapping CloneMapping(OutputMapping value) => new()
        { ColumnName = value.ColumnName, ElementKey = value.ElementKey };

    private static QueryRuleDefinition CloneRule(QueryRuleDefinition value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        SqlTemplate = value.SqlTemplate,
        IsEnabled = value.IsEnabled,
        ResultMode = value.ResultMode,
        OutputMappings = value.OutputMappings.Select(CloneMapping).ToList(),
        DisplayOrder = value.DisplayOrder,
        TimeoutSeconds = value.TimeoutSeconds,
        MaxRows = value.MaxRows
    };

    private sealed record ParsedCandidate(QueryRuleDefinition Rule, ParsedSimpleRule? Parsed);
    private sealed record ParsedSimpleRule(QueryRuleDefinition Rule, string FromClause, string FilterClause,
        string OrderByClause, bool TakeFirst, IReadOnlyList<KeyValuePair<string, string>> SelectExpressions,
        string SourceKey, string SourceDisplayName);

    [GeneratedRegex(@"^\s*SELECT\s+(?<select>.*?)\s+FROM\s+(?<from>.*?)(?:\s+WHERE\s+(?<where>.*?))?(?:\s+ORDER\s+BY\s+(?<order>.*?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SimpleSelectRegex();

    [GeneratedRegex(@"^TOP\s+1\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopOneRegex();

    [GeneratedRegex(@"^\s*SELECT\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingSelectRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
