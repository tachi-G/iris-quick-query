using System.Text.RegularExpressions;

namespace IrisQuickQuery.Core.Services;

public static partial class SqlSelectListParser
{
    public static IReadOnlyList<string> GetResultColumnNames(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return [];
        var selectList = FindTopLevelSelectList(sql);
        if (string.IsNullOrWhiteSpace(selectList)) return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawExpression in SplitTopLevelExpressions(selectList))
        {
            var name = ExtractResultName(rawExpression);
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);
        }
        return result;
    }

    private static string FindTopLevelSelectList(string sql)
    {
        var state = ScanState.Normal;
        var depth = 0;
        var selectStart = -1;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            switch (state)
            {
                case ScanState.Normal when ch == '\'' : state = ScanState.SingleQuote; break;
                case ScanState.Normal when ch == '"' : state = ScanState.DoubleQuote; break;
                case ScanState.Normal when ch == '[' : state = ScanState.Bracket; break;
                case ScanState.Normal when ch == '-' && next == '-' : state = ScanState.LineComment; i++; break;
                case ScanState.Normal when ch == '/' && next == '*' : state = ScanState.BlockComment; i++; break;
                case ScanState.Normal when ch == '(' : depth++; break;
                case ScanState.Normal when ch == ')' : depth = Math.Max(0, depth - 1); break;
                case ScanState.Normal when depth == 0 && selectStart < 0 && IsKeywordAt(sql, i, "SELECT"):
                    selectStart = i + 6; i += 5; break;
                case ScanState.Normal when depth == 0 && selectStart >= 0 && IsKeywordAt(sql, i, "FROM"):
                    return sql[selectStart..i];
                case ScanState.SingleQuote when ch == '\'' && next == '\'' : i++; break;
                case ScanState.SingleQuote when ch == '\'' : state = ScanState.Normal; break;
                case ScanState.DoubleQuote when ch == '"' && next == '"' : i++; break;
                case ScanState.DoubleQuote when ch == '"' : state = ScanState.Normal; break;
                case ScanState.Bracket when ch == ']' : state = ScanState.Normal; break;
                case ScanState.LineComment when ch is '\r' or '\n' : state = ScanState.Normal; break;
                case ScanState.BlockComment when ch == '*' && next == '/' : state = ScanState.Normal; i++; break;
            }
        }
        return selectStart >= 0 ? sql[selectStart..] : string.Empty;
    }

    private static IEnumerable<string> SplitTopLevelExpressions(string selectList)
    {
        var state = ScanState.Normal;
        var depth = 0;
        var start = 0;
        for (var i = 0; i < selectList.Length; i++)
        {
            var ch = selectList[i];
            var next = i + 1 < selectList.Length ? selectList[i + 1] : '\0';
            switch (state)
            {
                case ScanState.Normal when ch == '\'' : state = ScanState.SingleQuote; break;
                case ScanState.Normal when ch == '"' : state = ScanState.DoubleQuote; break;
                case ScanState.Normal when ch == '[' : state = ScanState.Bracket; break;
                case ScanState.Normal when ch == '-' && next == '-' : state = ScanState.LineComment; i++; break;
                case ScanState.Normal when ch == '/' && next == '*' : state = ScanState.BlockComment; i++; break;
                case ScanState.Normal when ch == '(' : depth++; break;
                case ScanState.Normal when ch == ')' : depth = Math.Max(0, depth - 1); break;
                case ScanState.Normal when ch == ',' && depth == 0:
                    yield return selectList[start..i]; start = i + 1; break;
                case ScanState.SingleQuote when ch == '\'' && next == '\'' : i++; break;
                case ScanState.SingleQuote when ch == '\'' : state = ScanState.Normal; break;
                case ScanState.DoubleQuote when ch == '"' && next == '"' : i++; break;
                case ScanState.DoubleQuote when ch == '"' : state = ScanState.Normal; break;
                case ScanState.Bracket when ch == ']' : state = ScanState.Normal; break;
                case ScanState.LineComment when ch is '\r' or '\n' : state = ScanState.Normal; break;
                case ScanState.BlockComment when ch == '*' && next == '/' : state = ScanState.Normal; i++; break;
            }
        }
        yield return selectList[start..];
    }

    private static string? ExtractResultName(string rawExpression)
    {
        var expression = CommentRegex().Replace(rawExpression, " ").Trim();
        expression = DistinctRegex().Replace(expression, string.Empty);
        expression = TopRegex().Replace(expression, string.Empty).Trim();
        if (expression.Length == 0 || expression == "*" || expression.EndsWith(".*", StringComparison.Ordinal)) return null;

        var alias = ExplicitAliasRegex().Match(expression);
        if (alias.Success) return Unquote(alias.Groups["alias"].Value);

        alias = ImplicitAliasRegex().Match(expression);
        if (alias.Success) return Unquote(alias.Groups["alias"].Value);

        var simpleColumn = QualifiedColumnRegex().Match(expression);
        if (simpleColumn.Success) return Unquote(simpleColumn.Groups["column"].Value);

        return expression.Length <= 80 && !expression.Any(char.IsWhiteSpace) ? expression : null;
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (index + keyword.Length > text.Length
            || !text.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        var before = index == 0 ? '\0' : text[index - 1];
        var after = index + keyword.Length >= text.Length ? '\0' : text[index + keyword.Length];
        return !IsIdentifierCharacter(before) && !IsIdentifierCharacter(after);
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '$' or '#';
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            return value[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    [GeneratedRegex(@"--[^\r\n]*|/\*[\s\S]*?\*/", RegexOptions.CultureInvariant)]
    private static partial Regex CommentRegex();
    [GeneratedRegex(@"^\s*DISTINCT\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistinctRegex();
    [GeneratedRegex(@"^\s*TOP\s+(?:\(\s*\d+\s*\)|\d+)(?:\s+PERCENT)?(?:\s+WITH\s+TIES)?\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopRegex();
    [GeneratedRegex("""\bAS\s+(?<alias>\[[^\]]+\]|"(?:""|[^"])+"|[A-Za-z_][A-Za-z0-9_$#]*)\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitAliasRegex();
    [GeneratedRegex("""\s+(?<alias>\[[^\]]+\]|"(?:""|[^"])+"|[A-Za-z_][A-Za-z0-9_$#]*)\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex ImplicitAliasRegex();
    [GeneratedRegex("""^(?:(?:\[[^\]]+\]|"(?:""|[^"])+"|[A-Za-z_][A-Za-z0-9_$#]*)\.)*(?<column>\[[^\]]+\]|"(?:""|[^"])+"|[A-Za-z_][A-Za-z0-9_$#]*)$""", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedColumnRegex();

    private enum ScanState { Normal, SingleQuote, DoubleQuote, Bracket, LineComment, BlockComment }
}
