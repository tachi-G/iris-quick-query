using IrisQuickQuery.Core.Models;
using System.Text.RegularExpressions;

namespace IrisQuickQuery.Core.Services;

public static class ConfigurationValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(ConfigurationSnapshot snapshot)
    {
        var issues = new List<ValidationIssue>();
        var duplicateKeys = snapshot.Elements.GroupBy(x => x.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1);
        foreach (var duplicate in duplicateKeys) issues.Add(new("element.duplicate", $"元素 key 重复：{duplicate.Key}"));
        var elements = snapshot.Elements
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var element in snapshot.Elements)
        {
            if (string.IsNullOrWhiteSpace(element.Key)
                || !(char.IsAsciiLetter(element.Key[0]) || element.Key[0] == '_')
                || !element.Key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
                issues.Add(new("element.key", $"元素 {element.Label} 的 key 无效。"));
            if (string.IsNullOrWhiteSpace(element.Label)) issues.Add(new("element.label", $"元素 {element.Key} 缺少显示名称。"));
            if (element.Width is < 160 or > 800) issues.Add(new("element.width", $"元素 {element.Label} 的宽度必须在 160–800 之间。"));
            if (!string.IsNullOrWhiteSpace(element.ValidationPattern))
            {
                try { _ = new Regex(element.ValidationPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException) { issues.Add(new("element.regex", $"元素 {element.Label} 的校验正则无效。")); }
            }
        }

        foreach (var rule in snapshot.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name)) issues.Add(new("rule.name", "存在未命名规则。"));
            CompiledSql? compiled = null;
            try { compiled = SqlTemplateCompiler.Compile(rule.SqlTemplate ?? string.Empty); }
            catch (FormatException ex) { issues.Add(new("rule.placeholder", $"规则 {rule.Name}：{ex.Message}")); }
            if (compiled is not null)
            {
                foreach (var key in compiled.ParameterKeys.Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !elements.ContainsKey(x)))
                    issues.Add(new("rule.input", $"规则 {rule.Name} 引用了不存在的元素 {key}。"));
            }
            foreach (var issue in SqlSafetyValidator.Validate(rule.SqlTemplate ?? string.Empty))
                issues.Add(issue with { Message = $"规则 {rule.Name}：{issue.Message}" });
            if (rule.OutputMappings.Count == 0) issues.Add(new("rule.output", $"规则 {rule.Name} 至少需要一个输出映射。"));
            foreach (var mapping in rule.OutputMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.ColumnName)) issues.Add(new("rule.column", $"规则 {rule.Name} 存在空结果列名。"));
                if (string.IsNullOrWhiteSpace(mapping.ElementKey) || !elements.ContainsKey(mapping.ElementKey))
                    issues.Add(new("rule.output-element", $"规则 {rule.Name} 输出到不存在的元素 {mapping.ElementKey}。"));
            }
            if (rule.TimeoutSeconds is < 1 or > 15) issues.Add(new("rule.timeout", $"规则 {rule.Name} 超时必须在 1–15 秒。"));
            if (rule.MaxRows is < 1 or > 500) issues.Add(new("rule.rows", $"规则 {rule.Name} 最大行数必须在 1–500。"));
        }

        foreach (var cycle in FindCycles(snapshot))
            issues.Add(new("rule.cycle", $"检测到受控循环：{string.Join(" → ", cycle)}", true));
        return issues;
    }

    private static IEnumerable<IReadOnlyList<string>> FindCycles(ConfigurationSnapshot snapshot)
    {
        var graph = snapshot.Elements
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var rule in snapshot.Rules.Where(x => x.IsEnabled))
        {
            CompiledSql compiled;
            try { compiled = SqlTemplateCompiler.Compile(rule.SqlTemplate ?? string.Empty); } catch { continue; }
            foreach (var input in compiled.ParameterKeys)
                foreach (var output in rule.OutputMappings.Select(x => x.ElementKey).Where(x => !string.IsNullOrWhiteSpace(x)))
                    if (graph.TryGetValue(input, out var edges) && graph.ContainsKey(output)) edges.Add(output);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Keys)
            foreach (var cycle in Walk(node)) yield return cycle;

        IEnumerable<IReadOnlyList<string>> Walk(string node)
        {
            if (active.Contains(node))
            {
                var index = stack.FindIndex(x => x.Equals(node, StringComparison.OrdinalIgnoreCase));
                var cycle = stack[index..].Append(node).ToArray();
                var signature = string.Join("|", cycle.Order(StringComparer.OrdinalIgnoreCase));
                if (emitted.Add(signature)) yield return cycle;
                yield break;
            }
            if (!seen.Add(node)) yield break;
            active.Add(node); stack.Add(node);
            foreach (var next in graph[node]) foreach (var cycle in Walk(next)) yield return cycle;
            stack.RemoveAt(stack.Count - 1); active.Remove(node);
        }
    }
}
