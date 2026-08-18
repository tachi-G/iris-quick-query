using System.Text;
using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public sealed record CompiledSql(string CommandText, IReadOnlyList<string> ParameterKeys);
public sealed record ValidationIssue(string Code, string Message, bool IsWarning = false);

public static class SqlTemplateCompiler
{
    public static CompiledSql Compile(string sql)
    {
        var output = new StringBuilder(sql.Length);
        var keys = new List<string>();
        var state = ScanState.Normal;

        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            switch (state)
            {
                case ScanState.Normal when ch == '\'' : state = ScanState.SingleQuote; output.Append(ch); break;
                case ScanState.Normal when ch == '"' : state = ScanState.DoubleQuote; output.Append(ch); break;
                case ScanState.Normal when ch == '[' : state = ScanState.Bracket; output.Append(ch); break;
                case ScanState.Normal when ch == '-' && next == '-' : state = ScanState.LineComment; output.Append(ch).Append(next); i++; break;
                case ScanState.Normal when ch == '/' && next == '*' : state = ScanState.BlockComment; output.Append(ch).Append(next); i++; break;
                case ScanState.Normal when ch == '{' && next == '{':
                {
                    var end = sql.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new FormatException("SQL 元素占位符缺少结束符 }}。");
                    var key = sql[(i + 2)..end].Trim();
                    if (!IsValidKey(key)) throw new FormatException($"无效的元素 key：{key}");
                    keys.Add(key);
                    output.Append('?');
                    i = end + 1;
                    break;
                }
                case ScanState.SingleQuote:
                    output.Append(ch);
                    if (ch == '\'' && next == '\'') { output.Append(next); i++; }
                    else if (ch == '\'') state = ScanState.Normal;
                    break;
                case ScanState.DoubleQuote:
                    output.Append(ch);
                    if (ch == '"' && next == '"') { output.Append(next); i++; }
                    else if (ch == '"') state = ScanState.Normal;
                    break;
                case ScanState.Bracket: output.Append(ch); if (ch == ']') state = ScanState.Normal; break;
                case ScanState.LineComment: output.Append(ch); if (ch is '\r' or '\n') state = ScanState.Normal; break;
                case ScanState.BlockComment:
                    output.Append(ch);
                    if (ch == '*' && next == '/') { output.Append(next); i++; state = ScanState.Normal; }
                    break;
                default: output.Append(ch); break;
            }
        }
        if (state == ScanState.BlockComment) throw new FormatException("SQL 块注释未结束。");
        return new CompiledSql(output.ToString(), keys);
    }

    public static string RenameElementKeys(string sql, IReadOnlyDictionary<string, string> renames)
    {
        if (string.IsNullOrEmpty(sql) || renames.Count == 0) return sql;
        var replacements = renames.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder(sql.Length);
        var state = ScanState.Normal;

        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            switch (state)
            {
                case ScanState.Normal when ch == '\'' : state = ScanState.SingleQuote; output.Append(ch); break;
                case ScanState.Normal when ch == '"' : state = ScanState.DoubleQuote; output.Append(ch); break;
                case ScanState.Normal when ch == '[' : state = ScanState.Bracket; output.Append(ch); break;
                case ScanState.Normal when ch == '-' && next == '-' : state = ScanState.LineComment; output.Append(ch).Append(next); i++; break;
                case ScanState.Normal when ch == '/' && next == '*' : state = ScanState.BlockComment; output.Append(ch).Append(next); i++; break;
                case ScanState.Normal when ch == '{' && next == '{':
                {
                    var end = sql.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (end < 0) { output.Append(ch); break; }
                    var key = sql[(i + 2)..end].Trim();
                    if (replacements.TryGetValue(key, out var replacement))
                        output.Append("{{").Append(replacement).Append("}}");
                    else
                        output.Append(sql, i, end + 2 - i);
                    i = end + 1;
                    break;
                }
                case ScanState.SingleQuote:
                    output.Append(ch);
                    if (ch == '\'' && next == '\'') { output.Append(next); i++; }
                    else if (ch == '\'') state = ScanState.Normal;
                    break;
                case ScanState.DoubleQuote:
                    output.Append(ch);
                    if (ch == '"' && next == '"') { output.Append(next); i++; }
                    else if (ch == '"') state = ScanState.Normal;
                    break;
                case ScanState.Bracket: output.Append(ch); if (ch == ']') state = ScanState.Normal; break;
                case ScanState.LineComment: output.Append(ch); if (ch is '\r' or '\n') state = ScanState.Normal; break;
                case ScanState.BlockComment:
                    output.Append(ch);
                    if (ch == '*' && next == '/') { output.Append(next); i++; state = ScanState.Normal; }
                    break;
                default: output.Append(ch); break;
            }
        }
        return output.ToString();
    }

    private static bool IsValidKey(string key)
        => key.Length > 0 && (char.IsAsciiLetter(key[0]) || key[0] == '_') && key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private enum ScanState { Normal, SingleQuote, DoubleQuote, Bracket, LineComment, BlockComment }
}

public static class SqlSafetyValidator
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    { "INSERT", "UPDATE", "DELETE", "MERGE", "CALL", "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT", "REVOKE" };

    public static IReadOnlyList<ValidationIssue> Validate(string sql)
    {
        var issues = new List<ValidationIssue>();
        IReadOnlyList<(string Token, int Depth)> tokens;
        try { tokens = Tokenize(sql); }
        catch (FormatException ex) { return [new("sql.syntax", ex.Message)]; }

        if (tokens.Count == 0) return [new("sql.empty", "SQL 不能为空。")];
        if (tokens.Any(x => x.Token == ";")) issues.Add(new("sql.multiple", "不允许分号或多条 SQL。"));
        foreach (var blocked in tokens.Where(x => Blocked.Contains(x.Token)).Select(x => x.Token).Distinct(StringComparer.OrdinalIgnoreCase))
            issues.Add(new("sql.write", $"只读规则不允许关键字 {blocked}。"));

        var first = tokens[0].Token;
        if (first.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) return issues;
        if (!first.Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("sql.root", "SQL 必须以 SELECT 或 WITH 开始。"));
            return issues;
        }
        var rootAfterWith = tokens.Skip(1).FirstOrDefault(x => x.Depth == 0 && x.Token.Equals("SELECT", StringComparison.OrdinalIgnoreCase));
        if (rootAfterWith == default) issues.Add(new("sql.cte", "WITH 语句最终必须执行 SELECT。"));
        return issues;
    }

    private static IReadOnlyList<(string Token, int Depth)> Tokenize(string sql)
    {
        var compiled = SqlTemplateCompiler.Compile(sql).CommandText;
        var result = new List<(string, int)>();
        var depth = 0;
        var state = 0;
        for (var i = 0; i < compiled.Length; i++)
        {
            var ch = compiled[i];
            var next = i + 1 < compiled.Length ? compiled[i + 1] : '\0';
            if (state == 1) { if (ch == '\'' && next == '\'') i++; else if (ch == '\'') state = 0; continue; }
            if (state == 2) { if (ch == '"' && next == '"') i++; else if (ch == '"') state = 0; continue; }
            if (state == 3) { if (ch is '\r' or '\n') state = 0; continue; }
            if (state == 4) { if (ch == '*' && next == '/') { i++; state = 0; } continue; }
            if (ch == '\'') { state = 1; continue; }
            if (ch == '"') { state = 2; continue; }
            if (ch == '-' && next == '-') { i++; state = 3; continue; }
            if (ch == '/' && next == '*') { i++; state = 4; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (ch == ';') { result.Add((";", depth)); continue; }
            if (!char.IsLetter(ch) && ch != '_') continue;
            var start = i;
            while (i + 1 < compiled.Length && (char.IsLetterOrDigit(compiled[i + 1]) || compiled[i + 1] is '_' or '%')) i++;
            result.Add((compiled[start..(i + 1)], depth));
        }
        if (state is 1 or 2 or 4) throw new FormatException("SQL 包含未结束的字符串、标识符或注释。");
        return result;
    }
}
