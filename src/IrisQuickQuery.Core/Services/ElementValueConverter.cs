using System.Globalization;
using System.Text.RegularExpressions;
using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Services;

public static class ElementValueConverter
{
    public static bool TryConvert(object? raw, ElementDefinition definition, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (raw is null || raw is DBNull) return true;

        try
        {
            if (raw is string text)
            {
                text = text.Trim();
                if (text.Length == 0) return true;
                var validationValue = definition.EffectiveDataType == ElementDataType.Date ? FormatDate(text) : text;
                if (!string.IsNullOrWhiteSpace(definition.ValidationPattern)
                    && !Regex.IsMatch(validationValue, definition.ValidationPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                {
                    error = $"{definition.Label}不符合配置的格式规则。";
                    return false;
                }
                raw = text;
            }

            value = definition.EffectiveDataType switch
            {
                ElementDataType.Text => Convert.ToString(raw, CultureInfo.InvariantCulture),
                ElementDataType.Int64 => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
                ElementDataType.Decimal => Convert.ToDecimal(raw, CultureInfo.InvariantCulture),
                ElementDataType.Date => ConvertDate(raw),
                ElementDataType.DateTime => ConvertDateTime(raw),
                ElementDataType.Boolean => ConvertBoolean(raw),
                _ => throw new ArgumentOutOfRangeException()
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentOutOfRangeException)
        {
            error = $"{definition.Label}无法转换为{definition.EffectiveDataType}：{ex.Message}";
            return false;
        }
    }

    public static DateOnly ConvertDate(object raw)
    {
        if (raw is DateOnly date) return date;
        if (raw is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
        if (raw is DateTimeOffset offset) return DateOnly.FromDateTime(offset.DateTime);

        var text = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("日期不能为空。");

        var compact = Regex.Match(text, @"^(?<date>\d{8})(?:\D|$)", RegexOptions.CultureInvariant);
        if (compact.Success)
        {
            var digits = compact.Groups["date"].Value;
            var leadingYear = int.Parse(digits[..4], CultureInfo.InvariantCulture);
            if (leadingYear is >= 1800 and <= 2999) return CreateDate(digits[..4], digits[4..6], digits[6..8]);
            var trailingYear = int.Parse(digits[4..], CultureInfo.InvariantCulture);
            if (trailingYear is >= 1800 and <= 2999)
            {
                var first = int.Parse(digits[..2], CultureInfo.InvariantCulture);
                var second = int.Parse(digits[2..4], CultureInfo.InvariantCulture);
                return first > 12
                    ? CreateDate(digits[4..], digits[2..4], digits[..2])
                    : CreateDate(digits[4..], digits[..2], digits[2..4]);
            }
        }

        var parts = Regex.Match(text,
            @"^(?<first>\d{1,4})\s*(?:年|[-/.])\s*(?<second>\d{1,2})\s*(?:月|[-/.])\s*(?<third>\d{1,4})\s*日?",
            RegexOptions.CultureInvariant);
        if (parts.Success)
        {
            var first = parts.Groups["first"].Value;
            var second = parts.Groups["second"].Value;
            var third = parts.Groups["third"].Value;
            if (first.Length == 4) return CreateDate(first, second, third);
            if (third.Length == 4)
            {
                var firstNumber = int.Parse(first, CultureInfo.InvariantCulture);
                var secondNumber = int.Parse(second, CultureInfo.InvariantCulture);
                return firstNumber > 12
                    ? CreateDate(third, second, first)
                    : secondNumber > 12
                        ? CreateDate(third, first, second)
                        : CreateDate(third, first, second);
            }
        }

        var cultures = new[] { CultureInfo.GetCultureInfo("zh-CN"), CultureInfo.InvariantCulture };
        foreach (var culture in cultures)
            if (DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return DateOnly.FromDateTime(parsed);
        throw new FormatException($"无法识别日期“{text}”。");
    }

    public static string FormatDate(object raw)
        => ConvertDate(raw).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly CreateDate(string year, string month, string day)
        => new(int.Parse(year, CultureInfo.InvariantCulture), int.Parse(month, CultureInfo.InvariantCulture),
            int.Parse(day, CultureInfo.InvariantCulture));

    private static DateTime ConvertDateTime(object raw) => raw switch
    {
        DateTime dateTime => dateTime,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        _ => DateTime.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces)
    };

    private static bool ConvertBoolean(object raw) => raw switch
    {
        bool boolean => boolean,
        string text when text is "1" or "是" or "Y" or "y" or "true" or "True" => true,
        string text when text is "0" or "否" or "N" or "n" or "false" or "False" => false,
        _ => Convert.ToBoolean(raw, CultureInfo.InvariantCulture)
    };
}
