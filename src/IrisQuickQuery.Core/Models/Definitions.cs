using System.Text.Json.Serialization;

namespace IrisQuickQuery.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ElementDataType { Text, Int64, Decimal, Date, DateTime, Boolean }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ElementRole { VisibleField, ListColumn, Hidden }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleResultMode { Scalar, List }

public sealed class ElementDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public ElementDataType DataType { get; set; } = ElementDataType.Text;
    private bool? _isDate;
    public bool IsDate { get => _isDate ?? DataType == ElementDataType.Date; set => _isDate = value; }
    [JsonIgnore]
    public ElementDataType EffectiveDataType => IsDate
        ? ElementDataType.Date
        : DataType == ElementDataType.Date ? ElementDataType.Text : DataType;
    public ElementRole Role { get; set; } = ElementRole.VisibleField;
    public string Group { get; set; } = "基本信息";
    public int DisplayOrder { get; set; }
    public int Width { get; set; } = 280;
    public bool CanInput { get; set; } = true;
    public bool IsSensitive { get; set; }
    public string? ValidationPattern { get; set; }
}

public sealed class OutputMapping
{
    public string ColumnName { get; set; } = string.Empty;
    public string ElementKey { get; set; } = string.Empty;
}

public sealed class QueryRuleDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string SqlTemplate { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public RuleResultMode ResultMode { get; set; } = RuleResultMode.Scalar;
    public List<OutputMapping> OutputMappings { get; set; } = [];
    public int DisplayOrder { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxRows { get; set; } = 500;
}

public sealed class ConfigurationSnapshot
{
    public const int CurrentSchemaVersion = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "默认配置";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ElementDefinition> Elements { get; set; } = [];
    public List<QueryRuleDefinition> Rules { get; set; } = [];
}
