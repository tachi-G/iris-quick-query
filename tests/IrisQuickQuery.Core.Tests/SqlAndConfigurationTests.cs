using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;

namespace IrisQuickQuery.Core.Tests;

public sealed class SqlAndConfigurationTests
{
    [Fact]
    public void Compiler_ReplacesOnlyRealPlaceholders_InOccurrenceOrder()
    {
        var result = SqlTemplateCompiler.Compile("SELECT '{{ignored}}' AS x FROM T -- {{comment}}\nWHERE A={{patient_id}} OR B={{patient_id}}");
        Assert.Equal(["patient_id", "patient_id"], result.ParameterKeys);
        Assert.Contains("A=? OR B=?", result.CommandText);
        Assert.Contains("'{{ignored}}'", result.CommandText);
    }

    [Fact]
    public void ElementKeySynchronizer_RenamesSqlPlaceholdersAndMappings_WithoutTouchingLiteralsOrComments()
    {
        var elementId = Guid.NewGuid();
        var source = new[] { new ElementDefinition { Id = elementId, Key = "IDCard", Label = "身份证" } };
        var current = new[] { new ElementDefinition { Id = elementId, Key = "IDCardNumber", Label = "身份证" } };
        var rule = new QueryRuleDefinition
        {
            SqlTemplate = "SELECT '{{IDCard}}' AS literal FROM T -- {{IDCard}}\nWHERE id={{ IDCard }}",
            OutputMappings = [new OutputMapping { ColumnName = "id", ElementKey = "IDCard" }]
        };

        var changes = ElementKeySynchronizer.Detect(source, current);
        var updated = ElementKeySynchronizer.Apply([rule], changes);

        Assert.Equal(1, updated);
        Assert.Contains("WHERE id={{IDCardNumber}}", rule.SqlTemplate);
        Assert.Contains("'{{IDCard}}'", rule.SqlTemplate);
        Assert.Contains("-- {{IDCard}}", rule.SqlTemplate);
        Assert.Equal("IDCardNumber", rule.OutputMappings.Single().ElementKey);
    }

    [Theory]
    [InlineData("UPDATE T SET A=1")]
    [InlineData("SELECT * FROM T; DELETE FROM T")]
    [InlineData("CALL DangerousProc()")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM T")]
    public void SafetyValidator_RejectsNonReadOnlySql(string sql)
        => Assert.Contains(SqlSafetyValidator.Validate(sql), x => !x.IsWarning);

    [Theory]
    [InlineData("SELECT A FROM T WHERE B={{input}}")]
    [InlineData("WITH x AS (SELECT A FROM T) SELECT * FROM x")]
    public void SafetyValidator_AllowsSelectAndCteSelect(string sql)
        => Assert.DoesNotContain(SqlSafetyValidator.Validate(sql), x => !x.IsWarning);

    [Fact]
    public void SelectListParser_ExtractsFinalSelectColumnsAndAliases()
    {
        const string sql = """
            WITH source AS (
                SELECT ignored_column FROM PatientSource
            )
            SELECT p.registration_no,
                   CONCAT(p.last_name, ',', p.first_name) AS patient_name,
                   p.visit_no visit_number,
                   p.*
            FROM source p
            """;

        var columns = SqlSelectListParser.GetResultColumnNames(sql);

        Assert.Equal(["registration_no", "patient_name", "visit_number"], columns);
    }

    [Fact]
    public void SelectListParser_HandlesQuotedAliasesAndSelectModifiers()
    {
        var columns = SqlSelectListParser.GetResultColumnNames(
            "SELECT DISTINCT TOP (10) t.ID_CARD_NO AS [证件号码], t.REG_NO \"registration_no\" FROM T t");

        Assert.Equal(["证件号码", "registration_no"], columns);
    }

    [Fact]
    public void ConfigurationValidator_WarnsButDoesNotRejectCycles()
    {
        var config = TestConfig.CreateElements("a", "b");
        config.Rules.Add(TestConfig.Rule("a-to-b", "a", "b"));
        config.Rules.Add(TestConfig.Rule("b-to-a", "b", "a"));
        var issues = ConfigurationValidator.Validate(config);
        Assert.Contains(issues, x => x.Code == "rule.cycle" && x.IsWarning);
        Assert.DoesNotContain(issues, x => !x.IsWarning);
    }

    [Fact]
    public void Converter_PreservesTextLeadingZeros_AndTrimsOuterWhitespace()
    {
        var element = new ElementDefinition { Key = "visit", Label = "就诊号", DataType = ElementDataType.Text };
        Assert.True(ElementValueConverter.TryConvert("  00123  ", element, out var value, out _));
        Assert.Equal("00123", value);
    }

    [Theory]
    [InlineData("2026-08-16 00:00:00")]
    [InlineData("20260816 00:00:00")]
    [InlineData("16082026")]
    [InlineData("08162026")]
    [InlineData("2026年8月16日")]
    [InlineData("16/08/2026")]
    [InlineData("08/16/2026")]
    public void Converter_NormalizesConfiguredDateFields(string raw)
    {
        var element = new ElementDefinition { Key = "birth_date", Label = "出生日期", IsDate = true };

        Assert.True(ElementValueConverter.TryConvert(raw, element, out var value, out var error), error);
        Assert.Equal(new DateOnly(2026, 8, 16), value);
        Assert.Equal(ElementDataType.Date, element.EffectiveDataType);
        Assert.Equal("2026-08-16", ElementValueConverter.FormatDate(value!));
    }

    [Fact]
    public void RunContext_StoresDateOnlyForConfiguredDateFields()
    {
        var snapshot = TestConfig.CreateElements("visit_date");
        snapshot.Elements.Single().IsDate = true;
        var context = new RunContext(snapshot, new Dictionary<string, object?>
        {
            ["visit_date"] = "2026/08/16 00:00:00"
        });

        Assert.Equal(new DateOnly(2026, 8, 16), context.Slots["visit_date"].EffectiveValue);
    }

    [Fact]
    public void ConfigurationValidator_ReportsDuplicateKeysWithoutThrowing()
    {
        var config = TestConfig.CreateElements("same", "same");
        var issues = ConfigurationValidator.Validate(config);
        Assert.Contains(issues, x => x.Code == "element.duplicate" && !x.IsWarning);
    }

    [Fact]
    public void ConfigurationValidator_TurnsIncompleteGridEditsIntoIssuesInsteadOfThrowing()
    {
        var config = new ConfigurationSnapshot
        {
            Elements =
            [
                new ElementDefinition
                {
                    Key = null!, Label = null!, Width = 0, ValidationPattern = "["
                }
            ],
            Rules =
            [
                new QueryRuleDefinition
                {
                    Name = null!, SqlTemplate = null!,
                    OutputMappings = [new OutputMapping { ColumnName = null!, ElementKey = null! }]
                }
            ]
        };

        var exception = Record.Exception(() => ConfigurationValidator.Validate(config));
        Assert.Null(exception);
        var issues = ConfigurationValidator.Validate(config);
        Assert.Contains(issues, x => x.Code == "element.key");
        Assert.Contains(issues, x => x.Code == "element.width");
        Assert.Contains(issues, x => x.Code == "element.regex");
        Assert.Contains(issues, x => x.Code == "rule.output-element");
    }
}

internal static class TestConfig
{
    public static ConfigurationSnapshot CreateElements(params string[] keys) => new()
    {
        Elements = keys.Select((key, i) => new ElementDefinition { Key = key, Label = key, DisplayOrder = i }).ToList()
    };

    public static QueryRuleDefinition Rule(string name, string input, string output, RuleResultMode mode = RuleResultMode.Scalar, int order = 0)
        => new()
        {
            Name = name, SqlTemplate = $"SELECT {output} FROM T WHERE X={{{{{input}}}}}", ResultMode = mode, DisplayOrder = order,
            OutputMappings = [new OutputMapping { ColumnName = output, ElementKey = output }]
        };
}
